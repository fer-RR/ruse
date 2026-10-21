using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using Voicemet.App.Audio;

namespace Voicemet.App;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            DemoMode = true;
        }

        if (e.Args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
        {
            UiTestMode = true;
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
            WriteResult("converter-test.txt", $"Voicemet converter test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--modifier-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestModifier();
            WriteResult("modifier-test.txt", $"Voicemet modifier test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--preset-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestPresetRoundTrip();
            WriteResult("preset-test.txt", $"Voicemet preset test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--filter-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestFilters();
            WriteResult("filter-test.txt", $"Voicemet filter test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--effects-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestEffects();
            WriteResult("effects-test.txt", $"Voicemet effects test: {r.Summary}");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--align-test", StringComparer.OrdinalIgnoreCase))
        {
            var r = LatencySelfTest.TestLatencyAlignment();
            WriteResult("align-test.txt", $"Voicemet alignment test: {r.Summary}");
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
                routeLine = $"Voicemet route test: {r.Summary}";
            }
            catch (Exception ex)
            {
                routeLine = $"Voicemet route test FAILED: {ex}";
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
                deviceLine = $"Voicemet device test: {r.Summary}";
            }
            catch (Exception ex)
            {
                deviceLine = $"Voicemet device test FAILED: {ex}";
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
                graphLine = $"Voicemet graph test: {r.Summary}";
            }
            catch (Exception ex)
            {
                graphLine = $"Voicemet graph test FAILED: {ex}";
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
                chainLine = $"Voicemet chain test: {r.Summary}";
            }
            catch (Exception ex)
            {
                chainLine = $"Voicemet chain test FAILED: {ex}";
            }

            WriteResult("chain-test.txt", chainLine);
            Shutdown();
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
                        var window = MainWindow as Voicemet.App.MainWindow;
                        result = window == null
                            ? "no main window -> FAIL"
                            : window.RunDropTargetSelfTest();
                    }
                    catch (Exception ex)
                    {
                        result = $"FAILED: {ex}";
                    }

                    WriteResult("uitest.txt", $"Voicemet UI test: {result}");
                    (MainWindow as Voicemet.App.MainWindow)?.Close();
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
            line = $"Voicemet latency self-test: {result.Summary}";
        }
        catch (Exception ex)
        {
            line = $"Voicemet latency self-test FAILED: {ex}";
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
            line = $"Voicemet bench: {r.Sources} src → {r.Outputs} out, {r.Seconds:0}s | " +
                   $"{perSecond:0} frames/s mixed | CPU {r.CpuMs:0} ms " +
                   $"({cpuPercent:0.0}% of one core) | GC alloc {allocPerSec:0.0} KiB/s.";
        }
        catch (Exception ex)
        {
            line = $"Voicemet bench FAILED: {ex}";
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

