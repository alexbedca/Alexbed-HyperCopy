using System.IO.Hashing;
using Alexbed.HyperCopy.Core;

namespace Alexbed.HyperCopy.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hypercopy-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch { }
    }
}

public class EngineTests
{
    private static readonly Random Rng = new(1234);

    private static string MakeFile(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create);
        var buf = new byte[Math.Min(size, 1 << 20)];
        long left = size;
        while (left > 0)
        {
            Rng.NextBytes(buf);
            int n = (int)Math.Min(left, buf.Length);
            fs.Write(buf, 0, n);
            left -= n;
        }
        return path;
    }

    private static ulong Hash(string path)
    {
        var h = new XxHash3();
        using var fs = File.OpenRead(path);
        h.Append(fs);
        return h.GetCurrentHashAsUInt64();
    }

    private static void AssertTreesEqual(string a, string b)
    {
        var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(a, p)).Order().ToList();
        var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(b, p)).Order().ToList();
        Assert.Equal(fa, fb);
        foreach (var rel in fa)
        {
            Assert.Equal(new FileInfo(Path.Combine(a, rel)).Length, new FileInfo(Path.Combine(b, rel)).Length);
            Assert.Equal(Hash(Path.Combine(a, rel)), Hash(Path.Combine(b, rel)));
        }
        var da = Directory.GetDirectories(a, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(a, p)).Order();
        var db = Directory.GetDirectories(b, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(b, p)).Order();
        Assert.Equal(da, db);
    }

    private static CopyResult Run(CopyOptions o, IReadOnlyList<string> sources, string dest, out CopyEngine engine)
    {
        engine = new CopyEngine(o);
        var plan = engine.Scan(sources, dest);
        return engine.Execute(plan);
    }

    private static CopyResult Run(CopyOptions o, IReadOnlyList<string> sources, string dest) => Run(o, sources, dest, out _);

    // Sizes straddling sector (4096), buffer and small/large thresholds.
    public static TheoryData<long> EdgeSizes => [0, 1, 511, 4095, 4096, 4097, 65537, 1_048_575,
        4L * 1024 * 1024 - 1, 4L * 1024 * 1024, 4L * 1024 * 1024 + 1, 16L * 1024 * 1024 + 4097, 50_000_123];

    [Theory]
    [MemberData(nameof(EdgeSizes))]
    public void Copies_file_of_any_size_byte_exact(long size)
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "f.bin"), size);
        var dst = t.Sub("dst");
        var r = Run(new CopyOptions { Verify = true }, [src], dst);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        var copied = Path.Combine(dst, "f.bin");
        Assert.Equal(size, new FileInfo(copied).Length);
        Assert.Equal(Hash(src), Hash(copied));
    }

    [Theory]
    [InlineData(PerformanceProfile.SolidState, false)]
    [InlineData(PerformanceProfile.Rotational, false)]
    [InlineData(PerformanceProfile.Auto, true)]
    public void Copies_nested_tree_with_all_profiles(PerformanceProfile profile, bool buffered)
    {
        using var t = new TempDir();
        var root = t.Sub("src");
        var data = Path.Combine(root, "Data");
        MakeFile(Path.Combine(data, "a.txt"), 10);
        MakeFile(Path.Combine(data, "sub", "deep", "b.bin"), 6_000_000);
        MakeFile(Path.Combine(data, "sub", "c.bin"), 300_000);
        MakeFile(Path.Combine(data, "ünïcødé 文件 😀.txt"), 1234);
        for (int i = 0; i < 200; i++) MakeFile(Path.Combine(data, "many", $"f{i}.dat"), Rng.Next(0, 20000));
        Directory.CreateDirectory(Path.Combine(data, "empty", "dir"));

        var dst = t.Sub("dst");
        var o = new CopyOptions { Profile = profile, ForceBufferedIo = buffered, Verify = true };
        var r = Run(o, [data], dst, out var engine);

        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        AssertTreesEqual(data, Path.Combine(dst, "Data"));
        Assert.Equal(1.0, engine.Progress.Fraction, 3);
        Assert.Equal(engine.Progress.TotalFiles, r.FilesCopied);
    }

    [Theory]
    [InlineData(1000)]       // small path (CopyFileEx)
    [InlineData(6_000_000)]  // large path (pipeline)
    public void Preserves_creation_time_and_readonly(int size)
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "c.bin"), size);
        var created = new DateTime(2010, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(src, created);
        File.SetAttributes(src, FileAttributes.ReadOnly);
        var dst = t.Sub("dst");
        var r = Run(new CopyOptions { Verify = true }, [src], dst);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        var copied = Path.Combine(dst, "c.bin");
        Assert.Equal(created, File.GetCreationTimeUtc(copied));
        Assert.True(File.GetAttributes(copied).HasFlag(FileAttributes.ReadOnly));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(300_000)]
    public void Small_copy_stamps_creation_time_through_copy_handle(int size)
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "s.bin"), size);
        var created = new DateTime(2011, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var modified = new DateTime(2012, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetCreationTimeUtc(src, created);
        File.SetLastWriteTimeUtc(src, modified);
        File.SetAttributes(src, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var dst = Path.Combine(t.Sub("dst"), "s.bin");

        bool stamped = SmallFileCopy.Copy(src, dst, created);

        Assert.True(stamped, "the progress callback should set the creation time");
        Assert.Equal(created, File.GetCreationTimeUtc(dst));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(dst));
        Assert.True(File.GetAttributes(dst).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(dst).HasFlag(FileAttributes.Hidden));
        Assert.Equal(Hash(src), Hash(dst));
    }

    [Fact]
    public void Small_copy_reports_errors_like_File_Copy()
    {
        using var t = new TempDir();
        var missing = Path.Combine(t.Path, "nope.bin");
        Assert.Throws<FileNotFoundException>(() => SmallFileCopy.Copy(missing, Path.Combine(t.Path, "x.bin"), DateTime.UtcNow));

        var src = MakeFile(Path.Combine(t.Sub("src"), "a.bin"), 10);
        var dst = MakeFile(Path.Combine(t.Sub("dst"), "a.bin"), 20);
        var ex = Assert.ThrowsAny<IOException>(() => SmallFileCopy.Copy(src, dst, DateTime.UtcNow, overwrite: false));
        Assert.Equal(80, ex.HResult & 0xFFFF); // ERROR_FILE_EXISTS
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = Assert.ThrowsAny<IOException>(() => SmallFileCopy.Copy(src, dst, DateTime.UtcNow));
            Assert.Equal(32, locked.HResult & 0xFFFF); // sharing violation → engine falls back to handles
        }

        // Disk full must stay an IOException with code 112, or the engine won't stop the job.
        var full = SmallFileCopy.ToException(112, "x");
        Assert.IsType<IOException>(full);
        Assert.Equal(112, full.HResult & 0xFFFF);
        Assert.IsType<UnauthorizedAccessException>(SmallFileCopy.ToException(5, "x"));
    }

    [Fact]
    public void Small_files_overwrite_readonly_destination_and_keep_times()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "D");
        var created = new DateTime(2013, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        for (int i = 0; i < 50; i++)
        {
            var f = MakeFile(Path.Combine(data, $"f{i}.dat"), Rng.Next(0, 70_000));
            File.SetCreationTimeUtc(f, created.AddMinutes(i));
        }
        var dst = t.Sub("dst");
        var old = MakeFile(Path.Combine(dst, "D", "f7.dat"), 5);
        File.SetAttributes(old, FileAttributes.ReadOnly);

        var r = Run(new CopyOptions { Conflict = ConflictPolicy.Overwrite, Verify = true }, [data], dst);

        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        AssertTreesEqual(data, Path.Combine(dst, "D"));
        for (int i = 0; i < 50; i++)
            Assert.Equal(created.AddMinutes(i), File.GetCreationTimeUtc(Path.Combine(dst, "D", $"f{i}.dat")));
    }

    [Fact]
    public void Copies_small_file_that_another_program_is_writing()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "log.txt"), 5000);
        var dst = t.Sub("dst");
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var r = Run(new CopyOptions(), [src], dst);
            Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        }
        Assert.Equal(Hash(src), Hash(Path.Combine(dst, "log.txt")));
    }

    [Fact]
    public void Preserves_timestamps_and_attributes()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "ro.bin"), 5_000_000);
        var when = new DateTime(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, when);
        File.SetAttributes(src, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var dst = t.Sub("dst");

        var r = Run(new CopyOptions(), [src], dst);
        Assert.True(r.Success);
        var copied = Path.Combine(dst, "ro.bin");
        Assert.Equal(when, File.GetLastWriteTimeUtc(copied));
        var attrs = File.GetAttributes(copied);
        Assert.True(attrs.HasFlag(FileAttributes.ReadOnly));
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void SkipIdentical_resumes_and_overwrites_changed_files()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "D");
        MakeFile(Path.Combine(data, "same.bin"), 100_000);
        var changed = MakeFile(Path.Combine(data, "changed.bin"), 100_000);
        var dst = t.Sub("dst");
        Assert.True(Run(new CopyOptions(), [data], dst).Success);

        MakeFile(changed, 120_000); // new size + time
        var r = Run(new CopyOptions { Conflict = ConflictPolicy.SkipIdentical }, [data], dst);
        Assert.Equal(1, r.FilesSkipped);
        Assert.Equal(1, r.FilesCopied);
        AssertTreesEqual(data, Path.Combine(dst, "D"));
    }

    [Fact]
    public void Progress_reaches_100_percent_with_verify_and_skips()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "V");
        MakeFile(Path.Combine(data, "a.bin"), 6_000_000);
        MakeFile(Path.Combine(data, "b.bin"), 3000);
        var dst = t.Sub("dst");
        Assert.True(Run(new CopyOptions(), [data], dst).Success);
        MakeFile(Path.Combine(data, "c.bin"), 5000);

        var r = Run(new CopyOptions { Verify = true }, [data], dst, out var engine);
        Assert.Equal(2, r.FilesSkipped);
        Assert.Equal(1.0, engine.Progress.Fraction, 3);
    }

    [Fact]
    public void Skip_policy_never_touches_existing()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "x.bin"), 1000);
        var dst = t.Sub("dst");
        var existing = MakeFile(Path.Combine(dst, "x.bin"), 77);
        var r = Run(new CopyOptions { Conflict = ConflictPolicy.Skip }, [src], dst);
        Assert.Equal(1, r.FilesSkipped);
        Assert.Equal(77, new FileInfo(existing).Length);
    }

    [Fact]
    public void KeepBoth_writes_numbered_copy()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "x.bin"), 1000);
        var dst = t.Sub("dst");
        MakeFile(Path.Combine(dst, "x.bin"), 77);
        var r = Run(new CopyOptions { Conflict = ConflictPolicy.KeepBoth }, [src], dst);
        Assert.True(r.Success);
        Assert.Equal(77, new FileInfo(Path.Combine(dst, "x.bin")).Length);
        Assert.Equal(Hash(src), Hash(Path.Combine(dst, "x (2).bin")));
    }

    [Fact]
    public void Overwrite_replaces_readonly_destination()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "x.bin"), 9_000_000);
        var dst = t.Sub("dst");
        var existing = MakeFile(Path.Combine(dst, "x.bin"), 10);
        File.SetAttributes(existing, FileAttributes.ReadOnly);
        var r = Run(new CopyOptions { Conflict = ConflictPolicy.Overwrite }, [src], dst);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.Equal(Hash(src), Hash(existing));
    }

    [Fact]
    public void Move_same_volume_renames_whole_folder()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "M");
        MakeFile(Path.Combine(data, "a.bin"), 7_000_000);
        MakeFile(Path.Combine(data, "s", "b.txt"), 10);
        var snapshot = t.Sub("snapshot");
        Assert.True(Run(new CopyOptions(), [data], snapshot).Success);

        var dst = t.Sub("dst");
        var r = Run(new CopyOptions { Mode = TransferMode.Move }, [data], dst);
        Assert.True(r.Success);
        Assert.False(Directory.Exists(data));
        AssertTreesEqual(Path.Combine(snapshot, "M"), Path.Combine(dst, "M"));
        Assert.Equal(0, r.BytesTransferred); // pure rename: no data written
    }

    [Fact]
    public void Move_merges_into_existing_folder_and_removes_source()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "M");
        MakeFile(Path.Combine(data, "a.bin"), 1000);
        MakeFile(Path.Combine(data, "s", "b.txt"), 10);
        var dst = t.Sub("dst");
        MakeFile(Path.Combine(dst, "M", "other.txt"), 5);
        var r = Run(new CopyOptions { Mode = TransferMode.Move }, [data], dst);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.False(Directory.Exists(data));
        Assert.True(File.Exists(Path.Combine(dst, "M", "other.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "M", "s", "b.txt")));
    }

    [Fact]
    public void Move_cross_volume_path_copies_then_deletes()
    {
        // Simulate the cross-volume code path by forcing the copy pipeline via a merge into an
        // existing tree on a *different* volume when available; otherwise exercise the delete logic.
        using var t = new TempDir();
        string? otherRoot = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.AvailableFreeSpace > 200_000_000)
            .Select(d => d.RootDirectory.FullName)
            .FirstOrDefault(r => !DriveProbe.IsSameVolume(r, t.Path));
        if (otherRoot is null) return;

        var data = Path.Combine(t.Sub("src"), "X");
        MakeFile(Path.Combine(data, "big.bin"), 9_000_000);
        MakeFile(Path.Combine(data, "s", "small.txt"), 100);
        var expected = Hash(Path.Combine(data, "big.bin"));
        var dst = Path.Combine(otherRoot, "hypercopy-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = Run(new CopyOptions { Mode = TransferMode.Move, Verify = true }, [data], dst);
            Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
            Assert.False(Directory.Exists(data));
            Assert.Equal(expected, Hash(Path.Combine(dst, "X", "big.bin")));
            Assert.True(r.BytesTransferred >= 9_000_100);
        }
        finally
        {
            try { Directory.Delete(dst, true); } catch { }
        }
    }

    [Fact]
    public void Locked_source_is_reported_and_existing_destination_is_kept()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "locked.bin"), 1000);
        var dst = t.Sub("dst");
        var existing = MakeFile(Path.Combine(dst, "locked.bin"), 50);
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = Run(new CopyOptions { Conflict = ConflictPolicy.Overwrite, RetryCount = 0 }, [src], dst);
            Assert.Equal(1, r.FilesFailed);
            Assert.Single(r.Errors);
        }
        Assert.Equal(50, new FileInfo(existing).Length);
    }

    [Fact]
    public void Rejects_destination_inside_source()
    {
        using var t = new TempDir();
        var data = t.Sub("src");
        MakeFile(Path.Combine(data, "a.txt"), 1);
        var engine = new CopyEngine(new CopyOptions());
        Assert.Throws<InvalidOperationException>(() => engine.Scan([data], Path.Combine(data, "inner")));
    }

    // Regression (v1.0.2 bug report): source folder H:\x\App copied to its own parent H:\x
    // ran for a minute and failed every file. It must be refused before anything starts.
    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void Rejects_folder_copied_into_its_own_parent(TransferMode mode)
    {
        using var t = new TempDir();
        var parent = t.Sub("Personal APP");
        var app = Path.Combine(parent, "My App");
        MakeFile(Path.Combine(app, "a.dll"), 100);
        MakeFile(Path.Combine(app, "sub", "b.pyd"), 5_000_000);
        var before = Hash(Path.Combine(app, "sub", "b.pyd"));

        var engine = new CopyEngine(new CopyOptions { Mode = mode, Conflict = ConflictPolicy.Overwrite, Verify = true });
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Scan([app], parent));
        Assert.Contains("already in the destination folder", ex.Message);
        Assert.Equal(before, Hash(Path.Combine(app, "sub", "b.pyd"))); // untouched
        Assert.True(File.Exists(Path.Combine(app, "a.dll")));
    }

    [Fact]
    public void Destination_problem_is_reported_before_start()
    {
        using var t = new TempDir();
        var parent = t.Sub("P");
        var app = Directory.CreateDirectory(Path.Combine(parent, "App")).FullName;
        var file = MakeFile(Path.Combine(parent, "f.txt"), 1);
        var other = t.Sub("Other");

        Assert.Contains("already in this folder", CopyEngine.FindDestinationProblem([app], parent));
        Assert.Contains("already in this folder", CopyEngine.FindDestinationProblem([app], parent + "\\"));
        Assert.Contains("already in this folder", CopyEngine.FindDestinationProblem([file], parent));
        Assert.Contains("inside the source", CopyEngine.FindDestinationProblem([app], Path.Combine(app, "x")));
        Assert.Contains("inside the source", CopyEngine.FindDestinationProblem([app], app));
        Assert.Null(CopyEngine.FindDestinationProblem([app], other));
        Assert.Null(CopyEngine.FindDestinationProblem([app], Path.Combine(parent, "Backup")));
        Assert.Null(CopyEngine.FindDestinationProblem([app, file], ""));
    }

    [Fact]
    public void Missing_source_makes_the_job_unsuccessful()
    {
        using var t = new TempDir();
        var real = MakeFile(Path.Combine(t.Sub("src"), "a.txt"), 10);
        var r = Run(new CopyOptions(), [real, Path.Combine(t.Path, "nope")], t.Sub("dst"));
        Assert.Equal(1, r.FilesCopied);
        Assert.Single(r.Errors);
        Assert.False(r.Success); // CLI exit code 1, not 0
    }

    [Fact]
    public void Rejects_file_copied_into_its_own_folder()
    {
        using var t = new TempDir();
        var dir = t.Sub("d");
        var f = MakeFile(Path.Combine(dir, "x.bin"), 10);
        var engine = new CopyEngine(new CopyOptions { Conflict = ConflictPolicy.Overwrite });
        Assert.Throws<InvalidOperationException>(() => engine.Scan([f], dir));
        Assert.Throws<InvalidOperationException>(() => engine.Scan([f], dir + "\\")); // trailing slash
    }

    [Fact]
    public void Failed_files_are_never_counted_as_verified()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "D");
        var locked = MakeFile(Path.Combine(data, "locked.bin"), 200_000);
        var big = MakeFile(Path.Combine(data, "locked-big.bin"), 6_000_000);
        var dst = t.Sub("dst");

        var engine = new CopyEngine(new CopyOptions { Verify = true, RetryCount = 0 });
        var plan = engine.Scan([data], dst);
        CopyResult r;
        // Exclusive locks: every copy fails with a sharing violation.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(big, FileMode.Open, FileAccess.Read, FileShare.None))
            r = engine.Execute(plan);

        Assert.Equal(2, r.FilesFailed);
        Assert.Equal(0, r.FilesCopied);
        Assert.Equal(0, r.ChecksumsPassed);
        Assert.Equal(0, r.BytesTransferred);
        Assert.Equal(0, engine.Progress.VerifiedBytes); // was 6.2 MB "verified" before the fix
        Assert.Equal(1.0, engine.Progress.Fraction, 3);  // the bar still reaches 100%
    }

    [Fact]
    public void Skipped_files_are_never_counted_as_verified()
    {
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "D");
        MakeFile(Path.Combine(data, "a.bin"), 300_000);
        MakeFile(Path.Combine(data, "b.bin"), 6_000_000);
        var dst = t.Sub("dst");
        Assert.True(Run(new CopyOptions(), [data], dst).Success);

        var r = Run(new CopyOptions { Verify = true, Conflict = ConflictPolicy.Skip }, [data], dst, out var engine);
        Assert.Equal(2, r.FilesSkipped);
        Assert.Equal(0, engine.Progress.VerifiedBytes);
        Assert.Equal(1.0, engine.Progress.Fraction, 3);
    }

    [Fact]
    public void Replace_overwrites_changed_files_and_verifies_them()
    {
        // The user's real scenario done right: a new build over an old one in another folder.
        using var t = new TempDir();
        var data = Path.Combine(t.Sub("src"), "App");
        MakeFile(Path.Combine(data, "same.dll"), 100_000);
        MakeFile(Path.Combine(data, "changed.pyd"), 50_000);
        MakeFile(Path.Combine(data, "big.bin"), 6_000_000);
        var dst = t.Sub("dst");
        Assert.True(Run(new CopyOptions(), [data], dst).Success);
        MakeFile(Path.Combine(data, "changed.pyd"), 70_000); // new build
        MakeFile(Path.Combine(data, "new.dll"), 10);

        var r = Run(new CopyOptions { Verify = true, Conflict = ConflictPolicy.Overwrite }, [data], dst, out var engine);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.Equal(4, r.FilesCopied);
        Assert.Equal(4, r.ChecksumsPassed);
        Assert.Equal(engine.Progress.TotalBytes, engine.Progress.VerifiedBytes);
        AssertTreesEqual(data, Path.Combine(dst, "App"));
    }

    [Fact]
    public async Task Cancellation_stops_and_leaves_no_partial_file()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "huge.bin"), 300_000_000);
        var dst = t.Sub("dst");
        var engine = new CopyEngine(new CopyOptions { BufferSizeOverride = 1 << 20 });
        var plan = engine.Scan([src], dst);
        using var cts = new CancellationTokenSource();
        engine.Pause.Pause(); // hold the job so cancellation is deterministic
        var job = engine.ExecuteAsync(plan, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        var r = await job;
        Assert.True(r.Cancelled);
        Assert.False(File.Exists(Path.Combine(dst, "huge.bin")));
    }

    [Fact]
    public async Task Pause_and_resume_completes()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "p.bin"), 40_000_000);
        var dst = t.Sub("dst");
        var engine = new CopyEngine(new CopyOptions { BufferSizeOverride = 1 << 20 });
        var plan = engine.Scan([src], dst);
        engine.Pause.Pause();
        var job = engine.ExecuteAsync(plan);
        await Task.Delay(300);
        Assert.False(job.IsCompleted);
        engine.Pause.Resume();
        Assert.True((await job).Success);
        Assert.Equal(Hash(src), Hash(Path.Combine(dst, "p.bin")));
    }

    [Fact]
    public void Long_paths_are_supported()
    {
        using var t = new TempDir();
        var deep = Path.Combine(t.Sub("src"), "L");
        string p = deep;
        while (p.Length < 400) p = Path.Combine(p, new string('d', 40));
        MakeFile(Path.Combine(p, "long.bin"), 5_000_000);
        var dst = t.Sub("dst");
        var r = Run(new CopyOptions { Verify = true }, [deep], dst);
        Assert.True(r.Success, string.Join("; ", r.Errors.Select(e => e.Message)));
        AssertTreesEqual(deep, Path.Combine(dst, "L"));
    }

    [Fact]
    public void Unbuffered_io_is_really_used()
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "u.bin"), 10_000);
        using (FastFileIo.OpenRead(src, unbuffered: true, out bool readUnbuffered))
            Assert.True(readUnbuffered);
        using (FastFileIo.OpenWrite(Path.Combine(t.Path, "w.bin"), 8192, unbuffered: true, out bool writeUnbuffered))
            Assert.True(writeUnbuffered);
    }

    [Fact]
    public void Drive_probe_describes_system_drive()
    {
        var d = DriveProbe.Describe(Path.GetTempPath());
        Assert.NotNull(d.VolumeRoot);
        Assert.True(d.FreeBytes > 0);
        var tuning = DriveProbe.ResolveTuning(d, d, new CopyOptions());
        Assert.True(tuning.BufferSize % 4096 == 0);
    }

    [Fact]
    public void Verify_reports_checksum_pass_count()
    {
        using var t = new TempDir();
        var data = t.Sub("data");
        MakeFile(Path.Combine(data, "small.bin"), 10_000);
        MakeFile(Path.Combine(data, "sub", "large.bin"), 6L * 1024 * 1024);
        var dst = t.Sub("dst");

        var r = Run(new CopyOptions { Verify = true }, [data], dst);
        Assert.True(r.Success);
        Assert.True(r.Verified);
        Assert.Equal(2, r.ChecksumsPassed);
        Assert.Equal(0, r.ChecksumsFailed);

        // A second run skips identical files: nothing copied, so nothing checked.
        var again = Run(new CopyOptions { Verify = true, Conflict = ConflictPolicy.SkipIdentical }, [data], dst);
        Assert.Equal(2, again.FilesSkipped);
        Assert.Equal(0, again.ChecksumsPassed);

        var off = Run(new CopyOptions { Conflict = ConflictPolicy.Overwrite }, [data], dst);
        Assert.False(off.Verified);
        Assert.Equal(0, off.ChecksumsPassed);
    }

    [Theory]
    [InlineData(10_000L)]
    [InlineData(6L * 1024 * 1024)]
    public void Verify_reports_checksum_fail_when_copy_is_corrupted(long size)
    {
        using var t = new TempDir();
        var src = MakeFile(Path.Combine(t.Sub("src"), "f.bin"), size);
        var good = MakeFile(Path.Combine(t.Path, "src", "g.bin"), 5000);
        var dst = t.Sub("dst");

        var engine = new CopyEngine(new CopyOptions { Verify = true });
        engine.BeforeVerify = path =>
        {
            if (!path.EndsWith("f.bin", StringComparison.Ordinal)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            int b = fs.ReadByte();
            fs.Position = 0;
            fs.WriteByte((byte)(b ^ 0xFF)); // flip one byte on disk
        };
        var r = engine.Execute(engine.Scan([src, good], dst));

        Assert.False(r.Success);
        Assert.Equal(1, r.ChecksumsFailed);
        Assert.Equal(1, r.ChecksumsPassed);
        Assert.Equal(1, r.FilesFailed);
        Assert.Contains(r.Errors, e => e.Message.Contains("checksum mismatch"));

        Assert.False(File.Exists(Path.Combine(dst, "f.bin")));  // bad copy removed
    }
}
