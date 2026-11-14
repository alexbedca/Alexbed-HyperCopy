using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace Alexbed.HyperCopy.App;

public partial class MainWindow : Window
{
    public static readonly IValueConverter NonEmptyToVis = new FuncConverter(v => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter InverseBoolToVis = new FuncConverter(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    private readonly MainViewModel _vm = new();
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        _vm.PropertyChanged += OnVmChanged;
        _vm.GraphChanged += DrawGraph;
        _vm.StateChanged += UpdateTaskbar;
        BarHost.SizeChanged += (_, _) => UpdateBar();

        StateChanged += (_, _) =>
        {
            // Borderless windows overhang the screen when maximised; pad them back in.
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
        };

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropZone.BorderBrush = (Brush)FindResource("Stroke");
        Drop += OnDrop;

        // Portable convenience: sources/destination can be passed on the command line
        // ("AlexbedHyperCopy.exe <src>... --to <dest>").
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        int to = args.FindIndex(a => a.Equals("--to", StringComparison.OrdinalIgnoreCase));
        if (to >= 0 && to + 1 < args.Count) { _vm.Destination = args[to + 1]; args.RemoveRange(to, 2); }
        _vm.AddSources(args);

        Loaded += async (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("HYPERCOPY_VERIFY") == "1")
                _vm.Verify = true;
            if (Environment.GetEnvironmentVariable("HYPERCOPY_AUTOSTART") == "1")
                _ = _vm.StartAsync(this);
            if (Environment.GetEnvironmentVariable("HYPERCOPY_SNAPSHOT") is { Length: > 0 } shot)
                await SnapshotAsync(shot);
        };
    }

    /// <summary>Test hook: render the window to a PNG (used for automated UI screenshots).</summary>
    private async Task SnapshotAsync(string path)
    {
        int delay = int.TryParse(Environment.GetEnvironmentVariable("HYPERCOPY_SNAPSHOT_DELAY"), out var d) ? d : 1500;
        await Task.Delay(delay);
        var dpi = VisualTreeHelper.GetDpi(this);
        var element = (FrameworkElement)Content;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var ctx = bg.RenderOpen())
            ctx.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        rtb.Render(bg);
        rtb.Render(element);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        await using (var fs = File.Create(path)) enc.Save(fs);
        _vm.Cancel();
        _forceClose = true;
        Application.Current.Shutdown();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Percent))
        {
            UpdateBar();
            UpdateTaskbar();
        }
        else if (e.PropertyName == nameof(MainViewModel.Status) && _vm.IsBusy)
        {
            Title = $"{_vm.PercentText}% · Alexbed HyperCopy";
        }
    }

    private void UpdateBar() => BarFill.Width = Math.Max(0, BarHost.ActualWidth * _vm.Percent / 100.0);

    private void UpdateTaskbar()
    {
        Taskbar.ProgressValue = _vm.Percent / 100.0;
        Taskbar.ProgressState = _vm.State switch
        {
            JobState.Scanning => TaskbarItemProgressState.Indeterminate,
            JobState.Running => TaskbarItemProgressState.Normal,
            JobState.Paused => TaskbarItemProgressState.Paused,
            _ => TaskbarItemProgressState.None,
        };
        if (_vm.IsBusy) Title = $"{_vm.PercentText}% · Alexbed HyperCopy";
        else Title = "Alexbed HyperCopy";
    }

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void DrawGraph()
    {
        var samples = _vm.SpeedSamples;
        double w = Graph.ActualWidth, h = Graph.ActualHeight;
        var line = new PointCollection();
        var fill = new PointCollection();
        if (samples.Count >= 2 && w > 0 && h > 0)
        {
            double max = Math.Max(samples.Max() * 1.15, 1);
            double top = 30; // leave room for the caption
            double step = w / Math.Max(samples.Count - 1, 1);
            fill.Add(new Point(0, h));
            for (int i = 0; i < samples.Count; i++)
            {
                var pt = new Point(i * step, h - (samples[i] / max) * (h - top) - 2);
                line.Add(pt);
                fill.Add(pt);
            }
            fill.Add(new Point((samples.Count - 1) * step, h));
        }
        GraphLine.Points = line;
        GraphFill.Points = fill;
    }

    // ── Drag & drop ──

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = _vm.IsEditable && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = ok ? (Brush)FindResource("AccentSolid") : (Brush)FindResource("Stroke");
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("Stroke");
        if (_vm.IsEditable && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _vm.AddSources(paths);
    }

    // ── Buttons ──

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Select files to transfer" };
        if (dlg.ShowDialog(this) == true) _vm.AddSources(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "Select folders to transfer" };
        if (dlg.ShowDialog(this) == true) _vm.AddSources(dlg.FolderNames);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _vm.Sources.Clear();

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SourceEntry s }) _vm.Sources.Remove(s);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the destination folder or drive" };
        if (Directory.Exists(_vm.Destination)) dlg.InitialDirectory = _vm.Destination;
        if (dlg.ShowDialog(this) == true) _vm.Destination = dlg.FolderName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await _vm.StartAsync(this);
    private void Pause_Click(object sender, RoutedEventArgs e) => _vm.TogglePause();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _vm.Cancel();
    private void NewJob_Click(object sender, RoutedEventArgs e) => _vm.Reset();

    private void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_vm.Destination))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vm.Destination}\"") { UseShellExecute = true });
    }

    private void CopyErrors_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, _vm.Errors.Select(x => $"{x.Path}\t{x.Message}"))); }
        catch { /* clipboard busy */ }
    }

    // ── Window chrome ──

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsBusy && !_forceClose)
        {
            var r = MessageBox.Show(this, "A transfer is still running. Cancel it and exit?\n\nFiles already copied are kept; the file in progress is removed.",
                "Alexbed HyperCopy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _vm.Cancel();
        }
        base.OnClosing(e);
    }

    private sealed class FuncConverter(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
