using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.IO.Hashing;

namespace Alexbed.HyperCopy.Core;

/// <summary>Raised when the destination runs out of space; the whole job stops.</summary>
public sealed class DestinationFullException(string message, Exception inner) : IOException(message, inner);

/// <summary>
/// Alexbed HyperCopy engine: scan → plan → execute.
/// Small files go through a parallel buffered pool; large files through unbuffered
/// reader/writer pipelines. Concurrency is tuned to the physical drives involved.
/// </summary>
public sealed class CopyEngine
{
    private const int ERROR_DISK_FULL = 112;
    private const int ERROR_HANDLE_DISK_FULL = 39;
    private const long Fat32MaxFile = 4L * 1024 * 1024 * 1024 - 1;

    private readonly CopyOptions _options;
    private readonly ConcurrentQueue<CopyError> _errors = new();

    /// <summary>Test hook: runs on the written file just before it is read back for verification.</summary>
    internal Action<string>? BeforeVerify { get; set; }
    private readonly ConcurrentBag<PipelinedFileCopier> _copierPool = new();
    private TuningProfile _tuning = null!;

    public CopyEngine(CopyOptions options) => _options = options;
    public CopyOptions Options => _options;
    public CopyProgress Progress { get; } = new();
    public PauseController Pause { get; } = new();

    // ───────────────────────────── Scan ─────────────────────────────

    public Task<CopyPlan> ScanAsync(IReadOnlyList<string> sources, string destinationDirectory, CancellationToken ct = default)
        => Task.Run(() => Scan(sources, destinationDirectory, ct), ct);

    public CopyPlan Scan(IReadOnlyList<string> sources, string destinationDirectory, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Select at least one file or folder to copy.");
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("Select a destination folder.");

        Progress.Phase = CopyPhase.Scanning;
        Progress.ResetScan();
        string destRoot = Path.GetFullPath(destinationDirectory);
        var plan = new CopyPlan { DestinationDirectory = destRoot };

        foreach (string raw in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (src.Length == 2 && src[1] == ':') src += "\\"; // "E:" → "E:\"

            if (File.Exists(src))
            {
                var fi = new FileInfo(src);
                string dst = Path.Combine(destRoot, fi.Name);
                int start = plan.Files.Count;
                plan.Files.Add(new CopyItem(src, dst, fi.Length, fi.Attributes, fi.CreationTimeUtc, fi.LastWriteTimeUtc));
                Progress.AddScanned(fi.Length);
                plan.Roots.Add(new RootEntry(src, dst, false, start, 1, plan.Directories.Count, 0, fi.Length));
            }
            else if (Directory.Exists(src))
            {
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));
                if (string.IsNullOrEmpty(name)) name = $"Drive {char.ToUpperInvariant(src[0])}";
                string dst = Path.Combine(destRoot, name);

                if (IsSameOrInside(destRoot, src))
                    throw new InvalidOperationException($"The destination is inside the source folder \"{src}\". Choose a different destination.");

                int fileStart = plan.Files.Count, dirStart = plan.Directories.Count;
                long bytes = ScanDirectory(src, dst, plan, ct);
                plan.Roots.Add(new RootEntry(src, dst, true, fileStart, plan.Files.Count - fileStart,
                    dirStart, plan.Directories.Count - dirStart, bytes));
            }
            else
            {
                plan.ScanErrors.Add(new CopyError(src, "Source not found."));
            }
        }

        plan.TotalBytes = plan.Roots.Sum(r => r.Bytes);
        plan.LargestFile = plan.Files.Count == 0 ? 0 : plan.Files.Max(f => f.Length);

        Directory.CreateDirectory(destRoot);
        plan.DestinationDrive = DriveProbe.Describe(destRoot);
        plan.SourceDrive = plan.Roots.Count > 0 ? DriveProbe.Describe(plan.Roots[0].Source) : null;
        plan.DestinationFreeBytes = plan.DestinationDrive.FreeBytes;
        plan.Tuning = DriveProbe.ResolveTuning(plan.SourceDrive, plan.DestinationDrive, _options);

        bool sameVolumeMove = _options.Mode == TransferMode.Move && plan.Roots.Count > 0 &&
                              plan.Roots.All(r => DriveProbe.IsSameVolume(r.Source, destRoot));
        if (sameVolumeMove)
            plan.DestinationFreeBytes = -1; // a rename needs no space

        if (plan.DestinationDrive.IsFat32 && plan.LargestFile > Fat32MaxFile)
        {
            int count = plan.Files.Count(f => f.Length > Fat32MaxFile);
            plan.Warnings.Add($"The destination drive is FAT32, which cannot store files of 4 GB or larger. " +
                              $"{count} file(s) will fail. Reformat the drive as exFAT or NTFS to copy them.");
        }
        if (!plan.HasEnoughSpace)
            plan.Warnings.Add($"Not enough free space: need {Format.Bytes(plan.TotalBytes)}, " +
                              $"destination has {Format.Bytes(plan.DestinationFreeBytes)}.");

        return plan;
    }

    private long ScanDirectory(string srcRoot, string dstRoot, CopyPlan plan, CancellationToken ct)
    {
        long bytes = 0;
        var stack = new Stack<(string Src, string Dst)>();
        var rootInfo = new DirectoryInfo(srcRoot);
        plan.Directories.Add(new DirectoryItem(srcRoot, dstRoot, rootInfo.Attributes, rootInfo.CreationTimeUtc, rootInfo.LastWriteTimeUtc));
        stack.Push((srcRoot, dstRoot));

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (srcDir, dstDir) = stack.Pop();
            var subdirs = new List<(string, string)>();
            try
            {
                var entries = new FileSystemEnumerable<(string Name, bool IsDir, long Len, FileAttributes Attr, DateTime C, DateTime W)>(
                    srcDir,
                    (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes,
                        e.CreationTimeUtc.UtcDateTime, e.LastWriteTimeUtc.UtcDateTime),
                    enumOptions);

                foreach (var e in entries)
                {
                    string s = Path.Join(srcDir, e.Name);
                    string d = Path.Join(dstDir, e.Name);
                    if (e.IsDir)
                    {
                        if ((e.Attr & FileAttributes.ReparsePoint) != 0)
                        {
                            plan.Warnings.Add($"Skipped junction/symbolic link folder: {s}");
                            continue;
                        }
                        plan.Directories.Add(new DirectoryItem(s, d, e.Attr, e.C, e.W));
                        subdirs.Add((s, d));
                    }
                    else
                    {
                        plan.Files.Add(new CopyItem(s, d, e.Len, e.Attr, e.C, e.W));
                        bytes += e.Len;
                        Progress.AddScanned(e.Len);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                plan.ScanErrors.Add(new CopyError(srcDir, ex.Message));
            }

            // Push in reverse so traversal order matches on-disk listing order.
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
        return bytes;
    }

    private static bool IsSameOrInside(string candidate, string folder)
    {
        string c = Path.TrimEndingDirectorySeparator(candidate) + Path.DirectorySeparatorChar;
        string f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return c.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────── Execute ───────────────────────────

    public Task<CopyResult> ExecuteAsync(CopyPlan plan, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Execute(plan, ct)); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "HyperCopy job" };
        t.Start();
        return tcs.Task;
    }

    public CopyResult Execute(CopyPlan plan, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new CopyResult();
        _errors.Clear();
        _tuning = plan.Tuning ?? DriveProbe.ResolveTuning(plan.SourceDrive, plan.DestinationDrive, _options);
        Progress.Reset(plan.TotalBytes, plan.Files.Count, _options.Verify);
        Progress.Phase = CopyPhase.Copying;
        foreach (var e in plan.ScanErrors) _errors.Enqueue(e);

        bool move = _options.Mode == TransferMode.Move;
        var pending = new List<CopyItem>(plan.Files.Count);
        var cleanupDirs = new List<DirectoryItem>();
        var dirsToCreate = new List<DirectoryItem>();

        try
        {
            foreach (var root in plan.Roots)
            {
                ct.ThrowIfCancellationRequested();
                var files = plan.Files.GetRange(root.FileStart, root.FileCount);
                var dirs = plan.Directories.GetRange(root.DirectoryStart, root.DirectoryCount);

                if (move && DriveProbe.IsSameVolume(root.Source, plan.DestinationDirectory))
                {
                    if (TryRenameRoot(root))
                    {
                        Progress.AddProcessedOnly(root.Bytes);
                        Progress.FilesCompleted(root.FileCount);
                        continue;
                    }
                    // Destination already exists: merge by renaming file by file (still instant).
                    dirsToCreate.AddRange(dirs);
                    foreach (var f in files) RenameOneWithRetry(f, ct);
                    cleanupDirs.AddRange(dirs);
                    continue;
                }

                dirsToCreate.AddRange(dirs);
                pending.AddRange(files);
                if (move) cleanupDirs.AddRange(dirs);
            }

            foreach (var d in dirsToCreate)
            {
                try { Directory.CreateDirectory(d.Destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _errors.Enqueue(new CopyError(d.Destination, ex.Message));
                }
            }

            var small = pending.Where(f => f.Length < _options.LargeFileThreshold).ToList();
            var large = pending.Where(f => f.Length >= _options.LargeFileThreshold).ToList();

            if (_tuning.Serial)
            {
                RunSmall(small, ct);
                RunLarge(large, ct);
            }
            else
            {
                var a = Task.Factory.StartNew(() => RunSmall(small, ct), TaskCreationOptions.LongRunning);
                var b = Task.Factory.StartNew(() => RunLarge(large, ct), TaskCreationOptions.LongRunning);
                Task.WaitAll(a, b);
            }

            Progress.Phase = CopyPhase.Finishing;
            Progress.CurrentFile = null;
            FinishDirectories(dirsToCreate, cleanupDirs, move);
            Progress.Phase = CopyPhase.Completed;
        }
        catch (Exception ex) when (Unwrap(ex) is OperationCanceledException)
        {
            result.Cancelled = true;
            Progress.Phase = CopyPhase.Cancelled;
        }
        catch (Exception ex) when (Unwrap(ex) is DestinationFullException full)
        {
            result.FatalError = full.Message;
            Progress.Phase = CopyPhase.Failed;
        }
        finally
        {
            while (_copierPool.TryTake(out var c)) c.Dispose();
            sw.Stop();
        }

        result.Elapsed = sw.Elapsed;
        result.FilesCopied = Progress.CompletedFiles;
        result.FilesSkipped = Progress.SkippedFiles;
        result.FilesFailed = Progress.FailedFiles;
        result.Verified = _options.Verify;
        result.ChecksumsPassed = Progress.ChecksumsPassed;
        result.ChecksumsFailed = Progress.ChecksumsFailed;
        result.BytesTransferred = Progress.TransferredBytes;
        result.Errors.AddRange(_errors);
        return result;
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: > 0 } ag)
        {
            var inner = ag.InnerExceptions.FirstOrDefault(e => e is DestinationFullException) ?? ag.InnerExceptions[0];
            ex = inner;
        }
        return ex;
    }

    private bool TryRenameRoot(RootEntry root)
    {
        try
        {
            if (root.IsDirectory)
            {
                if (Directory.Exists(root.Destination) || File.Exists(root.Destination)) return false;
                Progress.CurrentFile = root.Source;
                Directory.Move(root.Source, root.Destination);
                return true;
            }
            if (File.Exists(root.Destination)) return false;
            Progress.CurrentFile = root.Source;
            File.Move(root.Source, root.Destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // e.g. a file inside is locked — fall back to per-file
        }
    }

    private void RenameOneWithRetry(CopyItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Pause.WaitIfPaused(ct);
        Progress.CurrentFile = item.Source;
        try
        {
            string? dest = ResolveDestination(item);
            if (dest is null)
            {
                Progress.AddProcessedOnly(item.Length);
                Progress.FileSkipped();
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(item.Source, dest, overwrite: true);
            Progress.AddProcessedOnly(item.Length);
            Progress.FileCompleted();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Progress.AddProcessedOnly(item.Length);
            Progress.FileFailed();
            _errors.Enqueue(new CopyError(item.Source, ex.Message));
        }
    }

    private void RunSmall(List<CopyItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var po = new ParallelOptions { MaxDegreeOfParallelism = _tuning.SmallFileThreads, CancellationToken = ct };
        Parallel.ForEach(items, po, item => ProcessFile(item, isLarge: false, ct));
    }

    private void RunLarge(List<CopyItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var po = new ParallelOptions { MaxDegreeOfParallelism = _tuning.LargeFileThreads, CancellationToken = ct };
        Parallel.ForEach(items, po, item => ProcessFile(item, isLarge: true, ct));
    }

    private void ProcessFile(CopyItem item, bool isLarge, CancellationToken ct)
    {
        int attempts = Math.Max(0, _options.RetryCount) + 1;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Pause.WaitIfPaused(ct);
            long reported = 0;
            string? dest = null;
            bool created = false;
            try
            {
                Progress.CurrentFile = item.Source;
                dest = ResolveDestination(item);
                if (dest is null)
                {
                    Progress.AddProcessedOnly(item.Length);
                    Progress.FileSkipped();
                    return;
                }

                void OnProgress(long n) { Interlocked.Add(ref reported, n); Progress.AddTransferred(n); }
                void OnCreated() => created = true;

                ulong srcHash = isLarge
                    ? CopyLarge(item, dest, OnProgress, OnCreated, ct)
                    : CopySmall(item, dest, OnProgress, OnCreated, ct);

                FastFileIo.ApplyAttributes(dest, item.Attributes);

                if (_options.Verify)
                {
                    BeforeVerify?.Invoke(dest);
                    VerifyCopy(dest, srcHash, ct);
                }

                // Balance the percentage when the file changed size since the scan.
                if (reported != item.Length) Progress.AddProcessedOnly(item.Length - reported);

                if (_options.Mode == TransferMode.Move)
                    DeleteSource(item.Source);

                if (_options.Verify) Progress.ChecksumPassed();
                Progress.FileCompleted();
                return;
            }
            catch (OperationCanceledException)
            {
                Progress.RevertTransferred(reported);
                if (created && dest is not null) TryDelete(dest);
                throw;
            }
            catch (Exception ex)
            {
                Progress.RevertTransferred(reported);
                if (created && dest is not null) TryDelete(dest);

                if (IsDiskFull(ex))
                    throw new DestinationFullException("The destination drive is full. Free up space and run the job again — files already copied will be skipped.", ex);

                if (attempt < attempts && IsRetryable(ex))
                {
                    Thread.Sleep(250 * attempt);
                    continue;
                }

                Progress.AddProcessedOnly(item.Length);
                Progress.FileFailed();
                if (ex is ChecksumMismatchException) Progress.ChecksumFailed();
                _errors.Enqueue(new CopyError(item.Source, Describe(ex)));
                return;
            }
        }
    }

    /// <summary>
    /// Small files: the kernel's CopyFileEx does open/copy/close in one round trip and was measured
    /// 3–5× faster per file than handle-based copying. It keeps data, attributes and modified time;
    /// the creation time is stamped through CopyFileEx's own handle (SmallFileCopy), or by path if that
    /// didn't happen. Files locked for writing by another program fall back to handles.
    /// </summary>
    private ulong CopySmall(CopyItem item, string dest, Action<long> onProgress, Action onCreated, CancellationToken ct)
    {
        ulong hash = _options.Verify ? HashSmallFile(item.Source) : 0;
        bool stamped;
        try
        {
            stamped = SmallFileCopy.Copy(item.Source, dest, item.CreationTimeUtc);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing / lock violation
        {
            return CopySmallBuffered(item, dest, onProgress, onCreated, ct);
        }
        onCreated();
        if (!stamped) RestoreCreationTime(dest, item.CreationTimeUtc);
        onProgress(item.Length);
        return hash;
    }

    private static ulong HashSmallFile(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long len = RandomAccess.GetLength(h);
        byte[] buf = ArrayPool<byte>.Shared.Rent((int)Math.Max(len, 1));
        try
        {
            var hasher = new XxHash3();
            long off = 0;
            int n;
            while ((n = RandomAccess.Read(h, buf, off)) > 0) { hasher.Append(buf.AsSpan(0, n)); off += n; }
            return hasher.GetCurrentHashAsUInt64();
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static void RestoreCreationTime(string path, DateTime creationUtc)
    {
        try
        {
            File.SetCreationTimeUtc(path, creationUtc);
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only copies can't be stamped; lift the flag briefly.
            try
            {
                var a = File.GetAttributes(path);
                File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                File.SetCreationTimeUtc(path, creationUtc);
                File.SetAttributes(path, a);
            }
            catch { /* best effort */ }
        }
        catch (IOException) { /* best effort */ }
    }

    private ulong CopySmallBuffered(CopyItem item, string dest, Action<long> onProgress, Action onCreated, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(item.Length, 4096, _options.LargeFileThreshold));
        try
        {
            using var src = File.OpenHandle(item.Source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            long length = RandomAccess.GetLength(src);
            using var dst = File.OpenHandle(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                FileOptions.None, length);
            onCreated();
            XxHash3? hasher = _options.Verify ? new XxHash3() : null;
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = RandomAccess.Read(src, buffer, offset);
                if (n == 0) break;
                hasher?.Append(buffer.AsSpan(0, n));
                RandomAccess.Write(dst, buffer.AsSpan(0, n), offset);
                offset += n;
                onProgress(n);
            }
            FastFileIo.CopyMetadata(dst, item);
            return hasher?.GetCurrentHashAsUInt64() ?? 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ulong CopyLarge(CopyItem item, string dest, Action<long> onProgress, Action onCreated, CancellationToken ct)
    {
        if (!_copierPool.TryTake(out var copier))
            copier = new PipelinedFileCopier(_tuning.BufferSize, Math.Max(_tuning.BufferCount, _tuning.WriteDepth + 2));
        try
        {
            return copier.Copy(item.Source, dest, item, _tuning, _options.Verify, onProgress, onCreated, Pause, ct);
        }
        finally
        {
            _copierPool.Add(copier);
        }
    }

    private void VerifyCopy(string dest, ulong expected, CancellationToken ct)
    {
        using var buffer = new AlignedBuffer(Math.Min(_tuning.BufferSize, 8 * 1024 * 1024));
        long verified = 0;
        try
        {
            ulong actual = FastFileIo.HashFile(dest, buffer, n => { verified += n; Progress.AddVerified(n); }, Pause, ct);
            if (actual != expected)
                throw new ChecksumMismatchException();
        }
        catch
        {
            Progress.AddVerified(-verified);
            throw;
        }
    }

    /// <summary>Applies the conflict policy. Returns the path to write, or null to skip.</summary>
    private string? ResolveDestination(CopyItem item)
    {
        string dest = item.Destination;
        if (string.Equals(Path.GetFullPath(item.Source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source and destination are the same file.");

        var existing = new FileInfo(dest);
        if (!existing.Exists) return dest;

        switch (_options.Conflict)
        {
            case ConflictPolicy.Skip:
                return null;
            case ConflictPolicy.SkipIdentical:
                // 2 s tolerance: FAT/exFAT store modified time with 2-second resolution.
                if (existing.Length == item.Length &&
                    Math.Abs((existing.LastWriteTimeUtc - item.LastWriteTimeUtc).TotalSeconds) <= 2)
                    return null;
                break;
            case ConflictPolicy.KeepBoth:
                return UniqueName(dest);
        }

        if ((existing.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            existing.Attributes = FileAttributes.Normal;
        return dest;
    }

    private static string UniqueName(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Join(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void DeleteSource(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private void FinishDirectories(List<DirectoryItem> created, List<DirectoryItem> sourceDirsToRemove, bool move)
    {
        // Deepest first, so a parent's timestamp is set after its children are written.
        for (int i = created.Count - 1; i >= 0; i--)
        {
            var d = created[i];
            try
            {
                Directory.SetCreationTimeUtc(d.Destination, d.CreationTimeUtc);
                Directory.SetLastWriteTimeUtc(d.Destination, d.LastWriteTimeUtc);
                var a = d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
                if (a != 0) File.SetAttributes(d.Destination, FileAttributes.Directory | a);
            }
            catch { /* best effort */ }
        }

        if (!move) return;
        for (int i = sourceDirsToRemove.Count - 1; i >= 0; i--)
        {
            try { Directory.Delete(sourceDirsToRemove[i].Source, recursive: false); }
            catch { /* not empty (skipped/failed files) or in use — leave it */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static bool IsDiskFull(Exception ex)
    {
        int code = ex.HResult & 0xFFFF;
        return ex is IOException && (code == ERROR_DISK_FULL || code == ERROR_HANDLE_DISK_FULL);
    }

    private static bool IsRetryable(Exception ex) =>
        ex is IOException and not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException;

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied. " + ex.Message,
        IOException { HResult: unchecked((int)0x80070020) } => "The file is in use by another program.",
        _ => ex.Message,
    };
}

/// <summary>The copy read back from the destination does not match the source.</summary>
public sealed class ChecksumMismatchException()
    : IOException("Verification failed: the copy does not match the source (checksum mismatch).");
