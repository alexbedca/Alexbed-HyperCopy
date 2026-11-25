using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Alexbed.HyperCopy.App;

/// <summary>
/// Test hook for the pre-release speed checks: with HYPERCOPY_PERF_LOG=&lt;file&gt; the app writes
/// start-up milestones (ms since the process started) and every moment the window stopped
/// responding (UI thread busy for more than 100 ms). Off by default: no cost, nothing written.
/// </summary>
internal static class PerfLog
{
    private static readonly string? Path_ = Environment.GetEnvironmentVariable("HYPERCOPY_PERF_LOG");
    private static readonly List<string> Lines = [];
    private static DispatcherTimer? _watch;
    private static long _last;
    private static double _maxStall;
    private static int _stalls;

    public static bool Enabled => Path_ is { Length: > 0 };

    private static double SinceStart =>
        (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    public static void Mark(string what)
    {
        if (!Enabled) return;
        lock (Lines) Lines.Add($"{SinceStart,8:0} ms  {what}");
    }

    /// <summary>Measures how late a 20 ms UI timer fires; lateness = time the window could not respond.</summary>
    public static void StartStallWatch()
    {
        if (!Enabled || _watch is not null) return;
        _last = Stopwatch.GetTimestamp();
        _watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
        _watch.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            double gap = Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds - 20;
            _last = now;
            if (gap > 100)
            {
                _stalls++;
                _maxStall = Math.Max(_maxStall, gap);
                Mark($"UI not responding for {gap:0} ms");
            }
        };
        _watch.Start();
    }

    public static void Flush()
    {
        if (!Enabled) return;
        Mark($"exit · stalls over 100 ms: {_stalls}, longest {_maxStall:0} ms");
        lock (Lines) File.WriteAllLines(Path_!, Lines);
    }
}
