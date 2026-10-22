using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using Ruse.App.Audio;

namespace Ruse.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>True when launched with --demo; MainWindow builds a sample graph.</summary>
    public static bool DemoMode { get; private set; }

    /// <summary>True when launched with --uitest; the window starts empty so the
    /// UI self-test is not disturbed by a saved session.</summary>
    public static bool UiTestMode { get; private set; }

    /// <summary>True when launched with --live-demo; builds a graph carrying
    /// real audio instead of restoring the saved session.</summary>
    public static bool LiveDemoMode { get; private set; }

    /// <summary>True when launched with --perf-test; starts from an empty
    /// canvas so the benchmark measures a known state.</summary>
    public static bool PerfTestMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Ruse.App.Diagnostics.AppLog.Initialize();

        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            DemoMode = true;
        }

        if (e.Args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
        {
            UiTestMode = true;

            // Keep the self-test completely away from the user's real settings
            // and saved session.
            AppSettings.OverrideDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Ruse-uitest");
        }

        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--bench", StringComparer.OrdinalIgnoreCase))
        {
            RunBench(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--converter-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestConverter();
            WriteResult("converter-test.txt", $"Ruse converter test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--modifier-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestModifier();
            WriteResult("modifier-test.txt", $"Ruse modifier test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--preset-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestPresetRoundTrip();
            WriteResult("preset-test.txt", $"Ruse preset test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--filter-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestFilters();
            WriteResult("filter-test.txt", $"Ruse filter test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--effects-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestEffects();
            WriteResult("effects-test.txt", $"Ruse effects test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--align-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestLatencyAlignment();
            WriteResult("align-test.txt", $"Ruse alignment test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--route-test", StringComparer.OrdinalIgnoreCase))
        {
            uint pid = (uint)GetIntArg(e.Args, "--pid", 0);
            double seconds = GetDoubleArg(e.Args, "--seconds", 3.0);
            string routeLine;
            try
            {
                var r = LatencySelfTest.TestLiveRoute(pid, seconds);
                routeLine = $"Ruse route test: {r.Summary}";
            }
            catch (Exception ex)
            {
                routeLine = $"Ruse route test FAILED: {ex}";
            }

            WriteResult("route-test.txt", routeLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--device-test", StringComparer.OrdinalIgnoreCase))
        {
            uint pid = (uint)GetIntArg(e.Args, "--pid", 0);
            double seconds = GetDoubleArg(e.Args, "--seconds", 3.0);
            string deviceLine;
            try
            {
                var r = LatencySelfTest.TestDeviceRoute(pid, seconds);
                deviceLine = $"Ruse device test: {r.Summary}";
            }
            catch (Exception ex)
            {
                deviceLine = $"Ruse device test FAILED: {ex}";
            }

            WriteResult("device-test.txt", deviceLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--graph-test", StringComparer.OrdinalIgnoreCase))
        {
            uint pid = (uint)GetIntArg(e.Args, "--pid", 0);
            double seconds = GetDoubleArg(e.Args, "--seconds", 3.0);
            string graphLine;
            try
            {
                var r = LatencySelfTest.TestGraphRoute(pid, seconds);
                graphLine = $"Ruse graph test: {r.Summary}";
            }
            catch (Exception ex)
            {
                graphLine = $"Ruse graph test FAILED: {ex}";
            }

            WriteResult("graph-test.txt", graphLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--chain-test", StringComparer.OrdinalIgnoreCase))
        {
            string chainLine;
            try
            {
                var r = LatencySelfTest.TestChainBreak();
                chainLine = $"Ruse chain test: {r.Summary}";
            }
            catch (Exception ex)
            {
                chainLine = $"Ruse chain test FAILED: {ex}";
            }

            WriteResult("chain-test.txt", chainLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--live-demo", StringComparer.OrdinalIgnoreCase))
        {
            // Carries real audio so the UI (meters, cable waveforms) can be
            // checked against a genuine signal. The window stays open.
            uint livePid = (uint)GetIntArg(e.Args, "--pid", 0);
            LiveDemoMode = true;
            base.OnStartup(e);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        (MainWindow as Ruse.App.MainWindow)?.BuildLiveGraph(livePid);
                    }
                    catch (Exception ex)
                    {
                        (MainWindow as Ruse.App.MainWindow)?.ShowStatus($"Live graph failed: {ex.Message}");
                    }
                }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        if (e.Args.Contains("--perf-test", StringComparer.OrdinalIgnoreCase))
        {
            uint perfPid = (uint)GetIntArg(e.Args, "--pid", 0);
            PerfTestMode = true;
            double seconds = GetDoubleArg(e.Args, "--seconds", 4.0);
            bool useSink = e.Args.Contains("--perf-sink", StringComparer.OrdinalIgnoreCase);
            base.OnStartup(e);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(async () =>
                {
                    string result;
                    try
                    {
                        var window = MainWindow as Ruse.App.MainWindow;
                        result = window == null
                            ? "no main window -> FAIL"
                            : await window.RunPerformanceTest(perfPid, seconds, useSink);
                    }
                    catch (Exception ex)
                    {
                        result = $"FAILED: {ex}";
                    }

                    WriteResult("perf-test.txt", $"Voicemet perf test: {result}");
                    (MainWindow as Ruse.App.MainWindow)?.Close();
                    Shutdown();
                }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }


        if (e.Args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
        {
            // Exercises the real window: layout, hit-testing and the drop
            // target logic all need a live visual tree.
            base.OnStartup(e);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() =>
                {
                    string result;
                    try
                    {
                        var window = MainWindow as Ruse.App.MainWindow;
                        result = window == null
                            ? "no main window -> FAIL"
                            : window.RunDropTargetSelfTest() + " | " + window.RunAppearanceSelfTest() +
                              " | " + window.RunAlertSelfTest();
                    }
                    catch (Exception ex)
                    {
                        result = $"FAILED: {ex}";
                    }

                    WriteResult("uitest.txt", $"Ruse UI test: {result}");
                    (MainWindow as Ruse.App.MainWindow)?.Close();
                    Shutdown();
                }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        base.OnStartup(e);
    }

    private static void RunSelfTest(string[] args)
    {
        int iterations = 200;
        int argIndex = Array.FindIndex(args, a => a.Equals("--iterations", StringComparison.OrdinalIgnoreCase));
        if (argIndex >= 0 && argIndex + 1 < args.Length && int.TryParse(args[argIndex + 1], out int parsed))
        {
            iterations = Math.Max(10, parsed);
        }

        string line;
        try
        {
            var result = LatencySelfTest.Run(iterations);
            line = $"Ruse latency self-test: {result.Summary}";
        }
        catch (Exception ex)
        {
            line = $"Ruse latency self-test FAILED: {ex}";
        }

        WriteResult("latency-selftest.txt", line);
    }

    private static void RunBench(string[] args)
    {
        int sources = GetIntArg(args, "--sources", 8);
        int outputs = GetIntArg(args, "--outputs", 4);
        double seconds = GetDoubleArg(args, "--seconds", 3.0);

        string line;
        try
        {
            var r = LatencySelfTest.Benchmark(sources, outputs, seconds);
            double perSecond = r.FramesMixed / r.Seconds;
            double cpuPercent = r.CpuMs / (r.Seconds * 1000.0) * 100.0 / Environment.ProcessorCount;
            double allocPerSec = r.AllocBytes / r.Seconds / 1024.0;
            line = $"Ruse bench: {r.Sources} src → {r.Outputs} out, {r.Seconds:0}s | " +
                   $"{perSecond:0} frames/s mixed | CPU {r.CpuMs:0} ms " +
                   $"({cpuPercent:0.0}% of one core) | GC alloc {allocPerSec:0.0} KiB/s.";
        }
        catch (Exception ex)
        {
            line = $"Ruse bench FAILED: {ex}";
        }

        WriteResult("bench-result.txt", line);
    }

    private static int GetIntArg(string[] args, string name, int fallback)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }

    private static double GetDoubleArg(string[] args, string name, double fallback)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length &&
               double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v
            : fallback;
    }

    private static void WriteResult(string fileName, string line)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, fileName), line);
        }
        catch
        {
        }
    }
}

