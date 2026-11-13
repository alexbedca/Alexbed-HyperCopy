using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Alexbed.HyperCopy.Core;

namespace Alexbed.HyperCopy.App;

public enum JobState { Idle, Scanning, Running, Paused, Done }

public sealed class SourceEntry
{
    public required string FullPath { get; init; }
    public required bool IsFolder { get; init; }
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(FullPath)) is { Length: > 0 } n ? n : FullPath;
    public string Glyph => IsFolder ? "" : "";
    public string Detail { get; init; } = "";
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class MainViewModel : Observable
{
    private const int GraphCapacity = 120;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Queue<(double T, long Bytes, double Work)> _window = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private CopyEngine? _engine;
    private CopyPlan? _plan;
    private CancellationTokenSource? _cts;
    private double _lastGraphSample = -1;
    private long _lastGraphBytes;

    public MainViewModel()
    {
        _timer.Tick += (_, _) => Tick();
        Sources.CollectionChanged += (_, _) => { Raise(nameof(HasSources)); Raise(nameof(CanStart)); Raise(nameof(SourcesSummary)); };
    }

    public ObservableCollection<SourceEntry> Sources { get; } = [];
    public ObservableCollection<CopyError> Errors { get; } = [];
    public List<double> SpeedSamples { get; } = [];
    public event Action? GraphChanged;

    public bool HasSources => Sources.Count > 0;
    public string SourcesSummary
    {
        get
        {
            int folders = Sources.Count(s => s.IsFolder), files = Sources.Count - folders;
            var parts = new List<string>();
            if (folders > 0) parts.Add($"{folders} folder{(folders == 1 ? "" : "s")}");
            if (files > 0) parts.Add($"{files} file{(files == 1 ? "" : "s")}");
            return parts.Count == 0 ? "Nothing selected" : string.Join(" · ", parts);
        }
    }

    // ── Settings ──
    private string _destination = "";
    public string Destination
    {
        get => _destination;
        set { if (Set(ref _destination, value)) { Raise(nameof(CanStart)); _ = RefreshDestinationInfo(); } }
    }

    private string _destinationInfo = "";
    public string DestinationInfo { get => _destinationInfo; private set => Set(ref _destinationInfo, value); }

    private TransferMode _mode = TransferMode.Copy;
    public TransferMode Mode { get => _mode; set { if (Set(ref _mode, value)) Raise(nameof(StartLabel)); } }

    private ConflictPolicy _conflict = ConflictPolicy.SkipIdentical;
    public ConflictPolicy Conflict { get => _conflict; set => Set(ref _conflict, value); }

    private PerformanceProfile _profile = PerformanceProfile.Auto;
    public PerformanceProfile Profile { get => _profile; set => Set(ref _profile, value); }

    private bool _verify;
    public bool Verify { get => _verify; set => Set(ref _verify, value); }

    public string StartLabel => Mode == TransferMode.Move ? "Start moving" : "Start copying";

    // ── Job state ──
    private JobState _state = JobState.Idle;
    public JobState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsBusy)); Raise(nameof(IsEditable)); Raise(nameof(CanStart));
            Raise(nameof(ShowWelcome)); Raise(nameof(ShowDashboard)); Raise(nameof(ShowRunControls));
            Raise(nameof(ShowDoneControls)); Raise(nameof(PauseLabel)); Raise(nameof(PauseGlyph));
            StateChanged?.Invoke();
        }
    }
    public event Action? StateChanged;

    public bool IsBusy => State is JobState.Scanning or JobState.Running or JobState.Paused;
    public bool IsEditable => !IsBusy;
    public bool CanStart => !IsBusy && HasSources && !string.IsNullOrWhiteSpace(Destination);
    public bool ShowWelcome => State == JobState.Idle;
    public bool ShowDashboard => State != JobState.Idle;
    public bool ShowRunControls => IsBusy;
    public bool ShowDoneControls => State == JobState.Done;
    public string PauseLabel => State == JobState.Paused ? "Resume" : "Pause";
    public string PauseGlyph => State == JobState.Paused ? "" : "";

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private Brush _statusBrush = Brushes.White;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    private string _route = "";
    public string Route { get => _route; private set => Set(ref _route, value); }
    private string _tuning = "";
    public string Tuning { get => _tuning; private set => Set(ref _tuning, value); }

    private double _percent;
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    private string _percentText = "0";
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    private string _speedText = "—";
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    private string _speedSub = "";
    public string SpeedSub { get => _speedSub; private set => Set(ref _speedSub, value); }
    private string _etaText = "—";
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    private string _elapsedText = "";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    private string _bytesText = "—";
    public string BytesText { get => _bytesText; private set => Set(ref _bytesText, value); }
    private string _bytesSub = "";
    public string BytesSub { get => _bytesSub; private set => Set(ref _bytesSub, value); }
    private string _filesText = "—";
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }
    private string _filesSub = "";
    public string FilesSub { get => _filesSub; private set => Set(ref _filesSub, value); }
    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    private double _peak;
    public bool HasErrors => Errors.Count > 0;

    // ── Commands ──

    public void AddSources(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = raw;
            if (Sources.Any(s => string.Equals(s.FullPath, p, StringComparison.OrdinalIgnoreCase))) continue;
            if (Directory.Exists(p))
                Sources.Add(new SourceEntry { FullPath = p, IsFolder = true, Detail = p });
            else if (File.Exists(p))
                Sources.Add(new SourceEntry { FullPath = p, IsFolder = false, Detail = $"{Format.Bytes(new FileInfo(p).Length)} · {Path.GetDirectoryName(p)}" });
        }
    }

    public async Task StartAsync(Window owner)
    {
        if (!CanStart) return;
        Errors.Clear(); Raise(nameof(HasErrors));
        SpeedSamples.Clear(); GraphChanged?.Invoke();
        _window.Clear(); _peak = 0; _lastGraphSample = -1; _lastGraphBytes = 0;
        ResetReadouts();

        var options = new CopyOptions { Mode = Mode, Conflict = Conflict, Profile = Profile, Verify = Verify };
        _engine = new CopyEngine(options);
        _cts = new CancellationTokenSource();
        State = JobState.Scanning;
        SetStatus("Scanning…", "AccentSolid");
        Route = ""; Tuning = "";
        _clock.Restart();
        _timer.Start();
        KeepAwake(true);

        try
        {
            _plan = await _engine.ScanAsync(Sources.Select(s => s.FullPath).ToList(), Destination, _cts.Token);
            foreach (var e in _plan.ScanErrors) Errors.Add(e);
            Raise(nameof(HasErrors));

            if (_plan.Files.Count == 0 && _plan.Directories.Count == 0)
            {
                Finish("Nothing to copy", "Warning");
                return;
            }

            Route = $"{_plan.SourceDrive?.VolumeRoot.TrimEnd('\\')} {_plan.SourceDrive?.KindLabel}  →  " +
                    $"{_plan.DestinationDrive?.VolumeRoot.TrimEnd('\\')} {_plan.DestinationDrive?.KindLabel}" +
                    (_plan.DestinationDrive?.FileSystem is { } fs ? $" · {fs}" : "");
            Tuning = _plan.Tuning?.Description ?? "";

            if (_plan.Warnings.Count > 0)
            {
                var important = _plan.Warnings.Where(w => !w.StartsWith("Skipped junction", StringComparison.Ordinal)).ToList();
                if (important.Count > 0)
                {
                    var answer = MessageBox.Show(owner,
                        string.Join("\n\n", important.Take(5)) + "\n\nContinue anyway?",
                        "Alexbed HyperCopy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes)
                    {
                        Finish("Cancelled", "Muted");
                        State = JobState.Idle;
                        return;
                    }
                }
            }

            State = JobState.Running;
            _clock.Restart();
            _window.Clear();
            var result = await _engine.ExecuteAsync(_plan, _cts.Token);
            Tick();
            foreach (var e in result.Errors.Skip(Errors.Count)) Errors.Add(e);
            Raise(nameof(HasErrors));

            if (result.Cancelled) Finish("Cancelled", "Warning");
            else if (result.FatalError is not null)
            {
                Finish("Stopped", "Danger");
                Errors.Insert(0, new CopyError(Destination, result.FatalError));
                Raise(nameof(HasErrors));
            }
            else if (result.FilesFailed > 0) Finish($"Finished with {result.FilesFailed} error{(result.FilesFailed == 1 ? "" : "s")}", "Warning");
            else Finish(Mode == TransferMode.Move ? "Move complete" : "Copy complete", "Success");

            ElapsedText = $"Took {Format.Duration(result.Elapsed)}";
            if (result.BytesTransferred > 0)
            {
                SpeedText = Format.Speed(result.AverageBytesPerSecond);
                SpeedSub = _peak > 0 ? $"average · peak {Format.Speed(_peak)}" : "average";
            }
            EtaText = Format.Duration(result.Elapsed);
            CurrentFile = result.FilesSkipped > 0
                ? $"{Format.Count(result.FilesSkipped)} identical/existing file(s) skipped"
                : "";
            if (result.Success) System.Media.SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException)
        {
            Finish("Cancelled", "Warning");
        }
        catch (Exception ex)
        {
            Finish("Could not start", "Danger");
            Errors.Add(new CopyError(Destination, ex.Message));
            Raise(nameof(HasErrors));
            MessageBox.Show(owner, ex.Message, "Alexbed HyperCopy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Finish(string status, string brushKey)
    {
        _timer.Stop();
        KeepAwake(false);
        SetStatus(status, brushKey);
        State = JobState.Done;
        Raise(nameof(EtaLabel));
    }

    public string EtaLabel => State == JobState.Done ? "TOTAL TIME" : "TIME LEFT";

    public void TogglePause()
    {
        if (_engine is null) return;
        if (State == JobState.Running)
        {
            _engine.Pause.Pause();
            State = JobState.Paused;
            SetStatus("Paused", "Warning");
        }
        else if (State == JobState.Paused)
        {
            _engine.Pause.Resume();
            State = JobState.Running;
            _window.Clear();
            SetStatus(Mode == TransferMode.Move ? "Moving…" : "Copying…", "AccentSolid");
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _engine?.Pause.Resume();
    }

    public void Reset()
    {
        if (IsBusy) return;
        State = JobState.Idle;
    }

    // ── Live readouts ──

    private void Tick()
    {
        if (_engine is null) return;
        var p = _engine.Progress;
        double now = _clock.Elapsed.TotalSeconds;

        if (State == JobState.Scanning)
        {
            Status = $"Scanning… {Format.Count(p.ScannedFiles)} files";
            BytesText = Format.Bytes(p.ScannedBytes);
            BytesSub = "found so far";
            return;
        }

        long total = p.TotalBytes;
        double fraction = p.Fraction;
        Percent = fraction * 100;
        PercentText = (fraction * 100).ToString(fraction >= 0.999 || fraction == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);

        if (State == JobState.Running && p.Phase == CopyPhase.Copying)
            SetStatus(Mode == TransferMode.Move ? "Moving…" : "Copying…", "AccentSolid");
        if (p.Phase == CopyPhase.Finishing) Status = "Finishing…";

        long transferred = p.TransferredBytes;
        double work = fraction * total * (Verify ? 2 : 1);

        if (State == JobState.Running)
        {
            _window.Enqueue((now, transferred, work));
            while (_window.Count > 2 && now - _window.Peek().T > 6) _window.Dequeue();

            var first = _window.Peek();
            double dt = now - first.T;
            if (dt > 0.4)
            {
                double speed = (transferred - first.Bytes) / dt;
                double workRate = (work - first.Work) / dt;
                SpeedText = Format.Speed(speed);
                double totalWork = total * (Verify ? 2.0 : 1.0);
                EtaText = workRate > 1 ? Format.Duration(TimeSpan.FromSeconds((totalWork - work) / workRate)) : "—";
            }

            if (_lastGraphSample < 0 || now - _lastGraphSample >= 0.5)
            {
                if (_lastGraphSample >= 0)
                {
                    double s = (transferred - _lastGraphBytes) / (now - _lastGraphSample);
                    _peak = Math.Max(_peak, s);
                    SpeedSamples.Add(s);
                    if (SpeedSamples.Count > GraphCapacity) SpeedSamples.RemoveAt(0);
                    GraphChanged?.Invoke();
                }
                _lastGraphSample = now;
                _lastGraphBytes = transferred;
            }
            SpeedSub = _peak > 0 ? $"peak {Format.Speed(_peak)}" : "";
            ElapsedText = $"Elapsed {Format.Duration(_clock.Elapsed)}";
        }

        BytesText = Format.Bytes(Math.Min(p.ProcessedBytes, total));
        BytesSub = $"of {Format.Bytes(total)}" + (Verify && p.VerifiedBytes > 0 ? $" · {Format.Bytes(p.VerifiedBytes)} verified" : "");
        long doneFiles = p.CompletedFiles + p.SkippedFiles + p.FailedFiles;
        FilesText = Format.Count(doneFiles);
        FilesSub = $"of {Format.Count(p.TotalFiles)}" + (p.FailedFiles > 0 ? $" · {p.FailedFiles} failed" : "") +
                   (p.SkippedFiles > 0 ? $" · {Format.Count(p.SkippedFiles)} skipped" : "");
        if (State is JobState.Running or JobState.Paused && p.CurrentFile is { } cf)
            CurrentFile = cf;
        Raise(nameof(EtaLabel));
    }

    private void ResetReadouts()
    {
        Percent = 0; PercentText = "0"; SpeedText = "—"; SpeedSub = ""; EtaText = "—"; ElapsedText = "";
        BytesText = "—"; BytesSub = ""; FilesText = "—"; FilesSub = ""; CurrentFile = "";
    }

    private void SetStatus(string text, string brushKey)
    {
        Status = text;
        StatusBrush = (Brush)Application.Current.Resources[brushKey];
    }

    private async Task RefreshDestinationInfo()
    {
        string dest = Destination;
        if (string.IsNullOrWhiteSpace(dest)) { DestinationInfo = ""; return; }
        try
        {
            var d = await Task.Run(() => DriveProbe.Describe(dest));
            if (dest != Destination) return;
            DestinationInfo = $"{d.VolumeRoot.TrimEnd('\\')}  ·  {d.KindLabel}" +
                              (d.FileSystem is { } fs ? $"  ·  {fs}" : "") +
                              (d.FreeBytes >= 0 ? $"  ·  {Format.Bytes(d.FreeBytes)} free" : "");
        }
        catch
        {
            DestinationInfo = "";
        }
    }

    // Keep the PC awake during long transfers (portable: no settings changed, released when done).
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    private static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? 0x80000000u | 0x00000001u : 0x80000000u);
}

/// <summary>Binds an enum property to a group of RadioButtons.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
