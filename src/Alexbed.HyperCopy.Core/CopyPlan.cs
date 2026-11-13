namespace Alexbed.HyperCopy.Core;

public sealed record CopyItem(
    string Source,
    string Destination,
    long Length,
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);

public sealed record DirectoryItem(
    string Source,
    string Destination,
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);

/// <summary>One user-selected source (file or folder) and the slice of the plan it produced.</summary>
public sealed record RootEntry(
    string Source,
    string Destination,
    bool IsDirectory,
    int FileStart,
    int FileCount,
    int DirectoryStart,
    int DirectoryCount,
    long Bytes);

public sealed record CopyError(string Path, string Message);

/// <summary>Result of the scan phase: everything that will be transferred.</summary>
public sealed class CopyPlan
{
    public required string DestinationDirectory { get; init; }
    public List<RootEntry> Roots { get; } = [];
    public List<CopyItem> Files { get; } = [];
    public List<DirectoryItem> Directories { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<CopyError> ScanErrors { get; } = [];
    public long TotalBytes { get; internal set; }
    public long LargestFile { get; internal set; }
    public DriveDescriptor? DestinationDrive { get; internal set; }
    public DriveDescriptor? SourceDrive { get; internal set; }
    public TuningProfile? Tuning { get; internal set; }

    /// <summary>Free space on the destination volume at scan time (-1 if unknown).</summary>
    public long DestinationFreeBytes { get; internal set; } = -1;

    public bool HasEnoughSpace => DestinationFreeBytes < 0 || DestinationFreeBytes >= TotalBytes;
}

public sealed class CopyResult
{
    public long FilesCopied { get; internal set; }
    public long FilesSkipped { get; internal set; }
    public long FilesFailed { get; internal set; }
    public long BytesTransferred { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }
    public bool Cancelled { get; internal set; }
    public string? FatalError { get; internal set; }
    public List<CopyError> Errors { get; } = [];

    public double AverageBytesPerSecond =>
        Elapsed.TotalSeconds > 0 ? BytesTransferred / Elapsed.TotalSeconds : 0;

    public bool Success => !Cancelled && FatalError is null && FilesFailed == 0;
}
