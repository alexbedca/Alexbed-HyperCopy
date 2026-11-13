namespace Alexbed.HyperCopy.Core;

public enum CopyPhase
{
    Idle,
    Scanning,
    Copying,
    Finishing,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// Lock-free progress counters, written by worker threads and polled by the UI.
/// </summary>
public sealed class CopyProgress
{
    private long _totalBytes, _processedBytes, _transferredBytes, _verifiedBytes;
    private long _totalFiles, _completedFiles, _skippedFiles, _failedFiles;
    private long _scannedFiles, _scannedBytes;
    private volatile string? _currentFile;
    private volatile CopyPhase _phase;
    private volatile bool _verifyEnabled;

    public CopyPhase Phase { get => _phase; internal set => _phase = value; }
    public string? CurrentFile { get => _currentFile; internal set => _currentFile = value; }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long TotalFiles => Interlocked.Read(ref _totalFiles);
    /// <summary>Bytes finished (copied, skipped or failed) — drives the percentage.</summary>
    public long ProcessedBytes => Interlocked.Read(ref _processedBytes);
    /// <summary>Bytes physically written — drives the speed readout.</summary>
    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);
    public long VerifiedBytes => Interlocked.Read(ref _verifiedBytes);
    public long CompletedFiles => Interlocked.Read(ref _completedFiles);
    public long SkippedFiles => Interlocked.Read(ref _skippedFiles);
    public long FailedFiles => Interlocked.Read(ref _failedFiles);
    public long ScannedFiles => Interlocked.Read(ref _scannedFiles);
    public long ScannedBytes => Interlocked.Read(ref _scannedBytes);

    /// <summary>Overall completion 0..1, including the verification pass when enabled.</summary>
    public double Fraction
    {
        get
        {
            long total = TotalBytes;
            long files = TotalFiles;
            if (total <= 0)
                return files <= 0 ? 0 : (double)(CompletedFiles + SkippedFiles + FailedFiles) / files;
            double work = ProcessedBytes + (_verifyEnabled ? VerifiedBytes : 0);
            double totalWork = total * (_verifyEnabled ? 2.0 : 1.0);
            return Math.Clamp(work / totalWork, 0, 1);
        }
    }

    internal void Reset(long totalBytes, long totalFiles, bool verify)
    {
        Interlocked.Exchange(ref _totalBytes, totalBytes);
        Interlocked.Exchange(ref _totalFiles, totalFiles);
        Interlocked.Exchange(ref _processedBytes, 0);
        Interlocked.Exchange(ref _transferredBytes, 0);
        Interlocked.Exchange(ref _verifiedBytes, 0);
        Interlocked.Exchange(ref _completedFiles, 0);
        Interlocked.Exchange(ref _skippedFiles, 0);
        Interlocked.Exchange(ref _failedFiles, 0);
        _verifyEnabled = verify;
    }

    internal void ResetScan()
    {
        Interlocked.Exchange(ref _scannedFiles, 0);
        Interlocked.Exchange(ref _scannedBytes, 0);
    }

    internal void AddScanned(long bytes)
    {
        Interlocked.Increment(ref _scannedFiles);
        Interlocked.Add(ref _scannedBytes, bytes);
    }

    internal void AddTransferred(long bytes)
    {
        Interlocked.Add(ref _transferredBytes, bytes);
        Interlocked.Add(ref _processedBytes, bytes);
    }

    /// <summary>Roll back bytes of a file attempt that failed.</summary>
    internal void RevertTransferred(long bytes)
    {
        Interlocked.Add(ref _transferredBytes, -bytes);
        Interlocked.Add(ref _processedBytes, -bytes);
    }

    /// <summary>Bytes finished without (more) data transfer: skipped, failed, renamed or size drift.
    /// They count for both halves of the work so the bar reaches 100% when verification is on.</summary>
    internal void AddProcessedOnly(long bytes)
    {
        Interlocked.Add(ref _processedBytes, bytes);
        if (_verifyEnabled) Interlocked.Add(ref _verifiedBytes, bytes);
    }
    internal void AddVerified(long bytes) => Interlocked.Add(ref _verifiedBytes, bytes);
    internal void FileCompleted() => Interlocked.Increment(ref _completedFiles);
    internal void FilesCompleted(long count) => Interlocked.Add(ref _completedFiles, count);
    internal void FileSkipped() => Interlocked.Increment(ref _skippedFiles);
    internal void FileFailed() => Interlocked.Increment(ref _failedFiles);
}

/// <summary>Cooperative pause gate checked between buffers and between files.</summary>
public sealed class PauseController
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);

    public bool IsPaused => !_gate.IsSet;
    public void Pause() => _gate.Reset();
    public void Resume() => _gate.Set();
    internal void WaitIfPaused(CancellationToken ct)
    {
        if (!_gate.IsSet)
            _gate.Wait(ct);
    }
}
