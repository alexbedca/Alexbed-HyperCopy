using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Alexbed.HyperCopy.Core;

/// <summary>Native memory aligned to 4 KB, as required by unbuffered (FILE_FLAG_NO_BUFFERING) I/O.</summary>
internal sealed unsafe class AlignedBuffer : IDisposable
{
    public const int Alignment = 4096;
    private void* _ptr;

    public AlignedBuffer(int size)
    {
        Size = DriveProbe.AlignUp(size, Alignment);
        _ptr = NativeMemory.AlignedAlloc((nuint)Size, Alignment);
    }

    public int Size { get; }
    public Span<byte> Span => new(_ptr, Size);

    public void Dispose()
    {
        if (_ptr != null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }
}

internal static class FastFileIo
{
    /// <summary>FILE_FLAG_NO_BUFFERING: bypass the Windows file cache.</summary>
    public const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    public static SafeFileHandle OpenRead(string path, bool unbuffered, out bool gotUnbuffered)
    {
        if (unbuffered)
        {
            try
            {
                gotUnbuffered = true;
                return File.OpenHandle(path, FileMode.Open, FileAccess.Read, ReadShare, NoBuffering | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException { HResult: unchecked((int)0x80070057) })
            {
                // Some file systems / filter drivers refuse unbuffered handles; fall back silently.
            }
        }
        gotUnbuffered = false;
        return File.OpenHandle(path, FileMode.Open, FileAccess.Read, ReadShare, FileOptions.SequentialScan);
    }

    public static SafeFileHandle OpenWrite(string path, long preallocate, bool unbuffered, out bool gotUnbuffered)
    {
        if (unbuffered)
        {
            try
            {
                gotUnbuffered = true;
                return File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, NoBuffering, preallocate);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException { HResult: unchecked((int)0x80070057) })
            {
            }
        }
        gotUnbuffered = false;
        return File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, preallocate);
    }

    /// <summary>Read until the span is full or EOF. Returns bytes read.</summary>
    public static int ReadFull(SafeFileHandle h, Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(h, buffer[total..], offset + total);
            if (n == 0) break;
            total += n;
            // Unbuffered reads return a short count only at EOF; a non-aligned short read means EOF too.
            if (n % AlignedBuffer.Alignment != 0) break;
        }
        return total;
    }

    public static void CopyMetadata(SafeFileHandle dst, CopyItem item)
    {
        try
        {
            File.SetCreationTimeUtc(dst, item.CreationTimeUtc);
            File.SetLastWriteTimeUtc(dst, item.LastWriteTimeUtc);
        }
        catch (IOException) { /* FAT/exFAT oddities: timestamps are best effort */ }
    }

    public const FileAttributes CopiedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
        FileAttributes.Archive | FileAttributes.NotContentIndexed;

    public static void ApplyAttributes(string path, FileAttributes source)
    {
        var attrs = source & CopiedAttributes;
        if (attrs != FileAttributes.Archive && attrs != 0)
            File.SetAttributes(path, attrs);
    }

    /// <summary>Hash a file as it sits on disk (unbuffered when possible, so we verify the media, not RAM).</summary>
    public static ulong HashFile(string path, AlignedBuffer buffer, Action<long>? onProgress, PauseController pause, CancellationToken ct)
    {
        using var h = OpenRead(path, unbuffered: true, out _);
        var hasher = new XxHash3();
        long offset = 0;
        var span = buffer.Span;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);
            int n = ReadFull(h, span, offset);
            if (n == 0) break;
            hasher.Append(span[..n]);
            offset += n;
            onProgress?.Invoke(n);
            if (n < span.Length) break;
        }
        return hasher.GetCurrentHashAsUInt64();
    }
}

/// <summary>
/// Copies one large file with a reader thread and a writer thread sharing a ring of aligned buffers.
/// Reads and writes overlap fully, both bypass the file cache, and the source is hashed in-flight.
/// </summary>
internal sealed class PipelinedFileCopier : IDisposable
{
    private readonly AlignedBuffer[] _buffers;

    public PipelinedFileCopier(int bufferSize, int bufferCount)
    {
        _buffers = new AlignedBuffer[Math.Max(2, bufferCount)];
        for (int i = 0; i < _buffers.Length; i++)
            _buffers[i] = new AlignedBuffer(bufferSize);
    }

    public AlignedBuffer FirstBuffer => _buffers[0];

    /// <returns>XXH3 of the source when <paramref name="computeHash"/>, else 0.</returns>
    public ulong Copy(string source, string destination, CopyItem item, TuningProfile tuning, bool computeHash,
        Action<long> onProgress, Action onCreated, PauseController pause, CancellationToken ct)
    {
        using var src = FastFileIo.OpenRead(source, tuning.UnbufferedRead, out _);
        long length = RandomAccess.GetLength(src);
        using var dst = FastFileIo.OpenWrite(destination, length, tuning.UnbufferedWrite, out bool dstUnbuffered);
        onCreated();

        using var free = new BlockingCollection<int>(_buffers.Length);
        using var full = new BlockingCollection<(int Index, int Count, long Offset)>(_buffers.Length);
        for (int i = 0; i < _buffers.Length; i++) free.Add(i);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;
        XxHash3? hasher = computeHash ? new XxHash3() : null;
        Exception? writerError = null;

        // Several writers keep multiple writes queued at the device (like CopyFileEx does),
        // so the disk never idles between requests. Writes are positional, so order doesn't matter.
        int writerCount = Math.Clamp(tuning.WriteDepth, 1, _buffers.Length - 1);
        var writers = new Thread[writerCount];
        for (int w = 0; w < writerCount; w++)
        {
            writers[w] = new Thread(() =>
            {
                try
                {
                    foreach (var (index, count, offset) in full.GetConsumingEnumerable(token))
                    {
                        var span = _buffers[index].Span;
                        int writeLen = count;
                        if (dstUnbuffered && count % AlignedBuffer.Alignment != 0)
                        {
                            // Unbuffered writes must be sector multiples: pad the tail, truncate afterwards.
                            writeLen = DriveProbe.AlignUp(count, AlignedBuffer.Alignment);
                            span[count..writeLen].Clear();
                        }
                        RandomAccess.Write(dst, span[..writeLen], offset);
                        onProgress(count);
                        free.Add(index);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref writerError, ex, null);
                    linked.Cancel();
                }
            })
            {
                IsBackground = true,
                Name = "HyperCopy writer",
                Priority = ThreadPriority.AboveNormal,
            };
            writers[w].Start();
        }

        long readOffset = 0;
        try
        {
            while (true)
            {
                pause.WaitIfPaused(token);
                int index = free.Take(token);
                var span = _buffers[index].Span;
                int n = FastFileIo.ReadFull(src, span, readOffset);
                if (n == 0)
                    break;
                hasher?.Append(span[..n]);
                full.Add((index, n, readOffset), token);
                readOffset += n;
                if (n < span.Length)
                    break;
            }
            full.CompleteAdding();
        }
        catch (OperationCanceledException) when (writerError is not null)
        {
            // fall through: rethrow the writer's real error below
        }
        catch
        {
            linked.Cancel();
            foreach (var w in writers) w.Join();
            throw;
        }

        foreach (var w in writers) w.Join();
        if (writerError is not null)
        {
            if (writerError is OperationCanceledException)
                ct.ThrowIfCancellationRequested();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();
        }

        ct.ThrowIfCancellationRequested();
        // Exact length (removes unbuffered tail padding / handles files that shrank).
        if (RandomAccess.GetLength(dst) != readOffset)
            RandomAccess.SetLength(dst, readOffset);
        FastFileIo.CopyMetadata(dst, item);
        return hasher?.GetCurrentHashAsUInt64() ?? 0;
    }

    public void Dispose()
    {
        foreach (var b in _buffers) b.Dispose();
    }
}
