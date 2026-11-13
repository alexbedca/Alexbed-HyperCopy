namespace Alexbed.HyperCopy.Core;

/// <summary>Whether sources are kept (copy) or removed after a successful transfer (move).</summary>
public enum TransferMode
{
    Copy,
    Move,
}

/// <summary>What to do when a destination file already exists.</summary>
public enum ConflictPolicy
{
    /// <summary>Skip when size and modified time match (resumable), otherwise overwrite.</summary>
    SkipIdentical,
    /// <summary>Always replace the destination file.</summary>
    Overwrite,
    /// <summary>Never touch an existing destination file.</summary>
    Skip,
    /// <summary>Keep the existing file and write the new one as "name (2).ext".</summary>
    KeepBoth,
}

/// <summary>I/O tuning profile. <see cref="Auto"/> probes the drives involved.</summary>
public enum PerformanceProfile
{
    Auto,
    /// <summary>SSD / NVMe: high parallelism.</summary>
    SolidState,
    /// <summary>Hard disks, USB sticks, SD cards: sequential, big buffers.</summary>
    Rotational,
}

public sealed class CopyOptions
{
    public TransferMode Mode { get; set; } = TransferMode.Copy;
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.SkipIdentical;
    public PerformanceProfile Profile { get; set; } = PerformanceProfile.Auto;

    /// <summary>Re-read every written file from disk and compare its XXH3 checksum with the source.</summary>
    public bool Verify { get; set; }

    /// <summary>Number of extra attempts for a file that fails with a transient I/O error.</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Files at or above this size use the unbuffered, pipelined large-file path.</summary>
    public long LargeFileThreshold { get; set; } = 4L * 1024 * 1024;

    /// <summary>Optional overrides (null = decided by the profile).</summary>
    public int? BufferSizeOverride { get; set; }
    public int? ThreadsOverride { get; set; }
    /// <summary>Concurrent writes kept in flight per large file.</summary>
    public int? WriteDepthOverride { get; set; }

    /// <summary>Disable unbuffered I/O entirely (diagnostics / exotic file systems).</summary>
    public bool ForceBufferedIo { get; set; }
}
