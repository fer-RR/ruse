/// <summary>
/// Meters part of the main window. The shared state lives in MainWindow.xaml.cs;
/// this file only holds the code for that area.
/// </summary>
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using Ruse.App.Ui;

namespace Ruse.App;

public partial class MainWindow
{
    // ---------- Meters ----------

    private readonly Dictionary<NodeViewModel, double> _meterSmoothed = new();

    // Cached brushes — recolouring a bar must not allocate a new brush each tick.
    private static readonly SolidColorBrush MeterLowBrush = Freeze(Color.FromRgb(0x39, 0xD9, 0x8A));
    private static readonly SolidColorBrush MeterMidBrush = Freeze(Color.FromRgb(0xE0, 0xC2, 0x3A));
    private static readonly SolidColorBrush MeterHighBrush = Freeze(Color.FromRgb(0xE0, 0x60, 0x3A));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Draws a per-node volume bar from the live audio level. Fast attack makes
    /// the bar jump on transients; slow release makes it fall smoothly so it
    /// reads like a real VU meter.
    /// </summary>
    private void UpdateMeters()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
    double worstLatency = 0;
    double worstBacklog = 0;
    bool anyRunning = false;
        bool anyClipping = false;
        bool anyLowLatency = false;

        bool demo = _demoLevels.Count > 0;

        foreach (var (vm, obj) in _nodeBacking)
        {
            float level;
            switch (obj)
            {
                case SourceNode s:
                    level = s.MeterLevel;
                    anyRunning |= s.IsRunning;

                    // How much captured audio is waiting. Anything above a few
                    // tens of milliseconds is heard as delay, so it is worth
                    // showing next to the output latency.
                    double backlog = s.BufferedMs;
                    if (backlog > worstBacklog)
                    {
                        worstBacklog = backlog;
                    }

                    break;
                case OutputNode o:
                    level = o.MeterLevel;
                    anyRunning |= o.IsRunning;
                    anyClipping |= o.Clipping;
                    if (o.IsRunning)
                    {
                        anyLowLatency |= o.LowLatencyActive;
                        double lat = o.EstimatedLatencyMs;
                        if (lat > worstLatency)
                        {
                            worstLatency = lat;
                        }
                    }
                    break;
                default:
                    level = 0f;
                    break;
            }

            if (demo)
            {
                // Synthetic, gently wavering levels so the bars animate.
                _demoLevels.TryGetValue(vm, out float baseLevel);
                if (baseLevel == 0f)
                {
                    baseLevel = (float)(0.2 + _demoRng.NextDouble() * 0.6);
                    _demoLevels[vm] = baseLevel;
                }

                double wobble = 0.75 + 0.25 * Math.Sin(Environment.TickCount / 220.0 + vm.GetHashCode());
                level = (float)(baseLevel * wobble);
            }

            // Perceptual scaling: a raw RMS of 0.3 should look "loud-ish".
            double target = Math.Min(1.0, Math.Pow(level, 0.6));

            _meterSmoothed.TryGetValue(vm, out double current);

            // Fast attack (rise quickly) / slow release (decay gently). The
            // meters refresh ten times a second, so the smoothing is a little
            // stronger to keep the bars from looking stepped.
            double smoothed = target > current
                ? current + (target - current) * 0.6
                : current + (target - current) * 0.2;

            _meterSmoothed[vm] = smoothed;

            if (double.IsNaN(smoothed) || smoothed < 0.001)
            {
                smoothed = 0;
            }

            if (_nodeVisuals.TryGetValue(vm, out var body) &&
                body.Tag is Rectangle bar && bar.RenderTransform is ScaleTransform scale)
            {
                if (Math.Abs(scale.ScaleX - smoothed) > 0.001)
                {
                    scale.ScaleX = smoothed;
                }

                // Green → amber → red as the signal approaches clipping.
                SolidColorBrush color = smoothed switch
                {
                    < 0.7 => MeterLowBrush,
                    < 0.9 => MeterMidBrush,
                    _ => MeterHighBrush,
                };

                if (!ReferenceEquals(bar.Fill, color))
                {
                    bar.Fill = color;
                }
            }
        }

        // Latency meter: fill relative to a 60ms full-scale, colour-shifted as it grows.
        // Display-only; reuses this existing tick so it costs nothing extra.
        double latencyFraction = Math.Clamp(worstLatency / 60.0, 0, 1);
        LatencyMeterScale.ScaleX = latencyFraction;
        LatencyMeterBar.Background = worstLatency switch
        {
            <= 0 => MeterLowBrush,
            < 20 => MeterLowBrush,
            < 40 => MeterMidBrush,
            _ => MeterHighBrush,
        };

        LatencyText.Text = worstLatency > 0 ? $"~{worstLatency:0} ms" : "";

        // The capture backlog is real, audible delay that is not part of the
        // output latency figure, so it is shown separately.
        if (worstBacklog > 5)
        {
            LatencyText.Text += $"  (+{worstBacklog:0} ms capture)";
        }

        // Low-latency chip: show only when at least one live output has it active.
        LowLatencyChip.Visibility = anyLowLatency ? Visibility.Visible : Visibility.Collapsed;

        // Peak-hold the clip indicator so a brief overload stays visible ~1s.
        if (anyClipping)
        {
            _clipHoldUntil = Environment.TickCount64 + 1000;
        }

        ClipIndicator.Visibility = Environment.TickCount64 < _clipHoldUntil
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetRunState(anyRunning);

        UpdateMinimapVisibility();
        _minimap?.InvalidateVisual();

        _meterCount++;
        _meterMs += (System.Diagnostics.Stopwatch.GetTimestamp() - start)
            / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
    }

    private long _clipHoldUntil;
    private string? _stopTrace;

    /// <summary>
    /// Measures what the app costs: CPU and allocations while idle, the same
    /// while a graph is routing real audio, and what the periodic UI work
    /// (device refresh, filtering, meters) adds. Used by --perf-test.
    ///
    /// The waits are async on purpose: the UI thread must keep pumping so the
    /// timers and the meters really run while we measure them.
    /// </summary>
    public async Task<string> RunPerformanceTest(uint processId, double seconds, bool useTestSink = false)
    {
        seconds = Math.Clamp(seconds, 1.0, 60.0);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var report = new List<string>
        {
            $"startup to first paint {_startupMs:0} ms",
        };

        // --- Idle: window open, no nodes ---
        double cpu0 = proc.TotalProcessorTime.TotalMilliseconds;
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        await Task.Delay((int)(seconds * 1000));
        double cpu1 = proc.TotalProcessorTime.TotalMilliseconds;
        long alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        report.Add(
            $"idle {seconds:0}s: CPU {cpu1 - cpu0:0} ms ({(cpu1 - cpu0) / (seconds * 10):0.0}% of a core), " +
            $"alloc {(alloc1 - alloc0) / 1024.0 / seconds:0.0} KiB/s");

        // --- Idle with the periodic session refresh stopped: the refresh walks
        //     every audio session on every render device, so it is the other
        //     candidate for the idle cost.
        _refreshTimer.Stop();
        double cpuR = proc.TotalProcessorTime.TotalMilliseconds;
        long allocR = GC.GetTotalAllocatedBytes(precise: true);
        await Task.Delay((int)(seconds * 1000));
        double cpuS = proc.TotalProcessorTime.TotalMilliseconds;
        long allocS = GC.GetTotalAllocatedBytes(precise: true);
        _refreshTimer.Start();
        report.Add(
            $"idle, no session refresh {seconds:0}s: CPU {cpuS - cpuR:0} ms ({(cpuS - cpuR) / (seconds * 10):0.0}% of a core), " +
            $"alloc {(allocS - allocR) / 1024.0 / seconds:0.0} KiB/s");

        // --- Routing: a graph carrying real audio ---
        string sourceFault = string.Empty;
        var source = AddSourceNode(new SourceNode("Perf source", SourceKind.Application, processId), 40, 120);
        if (source != null && _nodeBacking.TryGetValue(source, out var sourceObj) && sourceObj is SourceNode sourceNode)
        {
            sourceNode.Faulted += (_, reason) => sourceFault = reason;
        }

        var second = AddSourceNode(new SourceNode("Perf source 2", SourceKind.Application, processId), 40, 320);
        var modifier = AudioProcessorCatalog.Find("highpass") is { } descriptor
            ? AddModifierNode(descriptor, 380, 120)
            : null;
        var output = useTestSink
            ? AddTestSinkOutput(720, 120)
            : AddOutputNode(FirstOutputItem(), 720, 120);
        string outputFault = string.Empty;
        if (output != null && _nodeBacking.TryGetValue(output, out var outputObj) && outputObj is OutputNode outputNode)
        {
            outputNode.Faulted += (_, reason) => outputFault = reason;
        }

        if (source != null && modifier != null)
        {
            ConnectDemo(source, modifier);
        }

        if (modifier != null && output != null)
        {
            ConnectDemo(modifier, output);
        }
        else if (source != null && output != null)
        {
            ConnectDemo(source, output);
        }

        if (second != null && output != null)
        {
            ConnectDemo(second, output);
        }

        UpdateGraphExtent();
        await Task.Delay(1200);

        double cpu2 = proc.TotalProcessorTime.TotalMilliseconds;
        long alloc2 = GC.GetTotalAllocatedBytes(precise: true);
        int refreshCount0 = _refreshCount;
        int filterCount0 = _filterCount;
        int meterCount0 = _meterCount;
        double refreshMs0 = _refreshMs;
        double filterMs0 = _filterMs;
        double meterMs0 = _meterMs;
        await Task.Delay((int)(seconds * 1000));
        double cpu3 = proc.TotalProcessorTime.TotalMilliseconds;
        long alloc3 = GC.GetTotalAllocatedBytes(precise: true);

        var src = source != null && _nodeBacking.TryGetValue(source, out var s) ? s as SourceNode : null;
        var outNode = output != null && _nodeBacking.TryGetValue(output, out var o) ? o as OutputNode : null;

        report.Add(
            $"routing {seconds:0}s: CPU {cpu3 - cpu2:0} ms ({(cpu3 - cpu2) / (seconds * 10):0.0}% of a core), " +
            $"alloc {(alloc3 - alloc2) / 1024.0 / seconds:0.0} KiB/s");

        report.Add(
            $"graph: autorun {_graph.AutoRun}, source running {src?.IsRunning}, meter {src?.MeterLevel:0.000}, " +
            $"output running {outNode?.IsRunning}, meter {outNode?.MeterLevel:0.000}, " +
            $"wrote {outNode?.BytesWritten / 1024.0:0} KiB" +
            $" | alloc capture {src?.CaptureAllocatedBytes / 1024.0:0} KiB, render {outNode?.RenderAllocatedBytes / 1024.0:0} KiB" +
            (string.IsNullOrEmpty(sourceFault) ? "" : $" | source fault: {sourceFault}") +
            (string.IsNullOrEmpty(outputFault) ? "" : $" | output fault: {outputFault}"));

        report.Add(
            $"periodic work over {seconds:0}s: refresh x{_refreshCount - refreshCount0} = {_refreshMs - refreshMs0:0} ms, " +
            $"filter x{_filterCount - filterCount0} = {_filterMs - filterMs0:0} ms, " +
            $"meters x{_meterCount - meterCount0} = {_meterMs - meterMs0:0} ms");

        // --- How much does the tiled background cost? ---
        var background = GraphHost.Background;
        GraphHost.Background = null;

        double cpuBg = proc.TotalProcessorTime.TotalMilliseconds;
        await Task.Delay((int)(seconds * 1000));
        double cpuBgEnd = proc.TotalProcessorTime.TotalMilliseconds;
        GraphHost.Background = background;
        report.Add(
            $"routing, no background {seconds:0}s: CPU {cpuBgEnd - cpuBg:0} ms ({(cpuBgEnd - cpuBg) / (seconds * 10):0.0}% of a core)");

        report.Add(
            $"render tier {System.Windows.Media.RenderCapability.Tier} (0=software, 1=partial, 2=hardware)");

        return string.Join(" | ", report);
    }

    /// <summary>Shows a message in the status bar (also used by diagnostics).</summary>
    public void ShowStatus(string text) => SetStatus(text);

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer.Stop();
        _refreshTimer.Stop();
        _graph.Dispose();
        base.OnClosed(e);
    }
}
