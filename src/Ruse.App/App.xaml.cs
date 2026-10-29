using System.IO;
using System.Threading;
using System.Windows;
using Ruse.App.Audio;
using Whisper.net;

namespace Ruse.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>True when launched with --demo; the Web UI builds a sample graph.</summary>
    public static bool DemoMode { get; private set; }

    /// <summary>True when launched with --live-demo; builds a graph carrying
    /// real audio instead of restoring the saved session.</summary>
    public static bool LiveDemoMode { get; private set; }

    /// <summary>The process whose audio --live-demo captures.</summary>
    public static uint LiveDemoProcessId { get; private set; }

    private const string InstanceMutexName = "Ruse.App.SingleInstance";
    private const string ActivateEventName = "Ruse.App.SingleInstance.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateRegistration;

    private bool TryClaimInstance()
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch
            {
                // The first instance may still be starting up.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainWindow is WebMainWindow window)
                {
                    window.RestoreFromTray();
                }
            })),
            null,
            Timeout.Infinite,
            false);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateRegistration?.Unregister(null);
        _activateEvent?.Dispose();
        _activateEvent = null;

        if (_instanceMutex != null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch
            {
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        int hostIndex = Array.FindIndex(e.Args, a => a.Equals("--vst3-host", StringComparison.OrdinalIgnoreCase));
        if (hostIndex >= 0 && hostIndex + 3 < e.Args.Length)
        {
            string state = hostIndex + 4 < e.Args.Length ? e.Args[hostIndex + 4] : "";
            Ruse.App.Diagnostics.AppLog.Initialize();
            Vst3HostRunner.Run(
                e.Args[hostIndex + 1],
                e.Args[hostIndex + 2],
                e.Args[hostIndex + 3],
                string.IsNullOrEmpty(state) ? null : state);
            Shutdown();
            return;
        }

        // Diagnostic and probe modes are intentionally allowed to run alongside
        // the app; only a normal launch claims the single-instance slot.
        if (e.Args.Length == 0 && !TryClaimInstance())
        {
            Shutdown();
            return;
        }

        if (e.Args.Contains("--gpu", StringComparer.OrdinalIgnoreCase))
        {
            ModelRuntime.UseGpu = true;
            ModelRuntime.ConfigureNativeLibraries();
        }

        int probeIndex = Array.FindIndex(e.Args, a => a.Equals("--model-probe", StringComparison.OrdinalIgnoreCase));
        if (probeIndex >= 0 && probeIndex + 2 < e.Args.Length)
        {
            RunModelProbe(e.Args[probeIndex + 1], e.Args[probeIndex + 2]);
            return;
        }

        int translationProbeIndex = Array.FindIndex(e.Args, a => a.Equals("--translation-probe", StringComparison.OrdinalIgnoreCase));
        if (translationProbeIndex >= 0 && translationProbeIndex + 2 < e.Args.Length)
        {
            TranslationModelProbe.Run(e.Args[translationProbeIndex + 1], e.Args[translationProbeIndex + 2]);
            return;
        }

        int translationTextIndex = Array.FindIndex(e.Args, a => a.Equals("--translation-text-test", StringComparison.OrdinalIgnoreCase));
        if (translationTextIndex >= 0 && translationTextIndex + 4 < e.Args.Length)
        {
            try
            {
                string translated = TranslationModelProbe.TranslateText(
                    e.Args[translationTextIndex + 1],
                    e.Args[translationTextIndex + 2],
                    e.Args[translationTextIndex + 3]);
                File.WriteAllText(e.Args[translationTextIndex + 4], translated);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[translationTextIndex + 4], "ERROR " + ex);
                Environment.Exit(3);
            }
        }

        int opusDownloadIndex = Array.FindIndex(e.Args, a => a.Equals("--opus-download-test", StringComparison.OrdinalIgnoreCase));
        if (opusDownloadIndex >= 0 && opusDownloadIndex + 2 < e.Args.Length)
        {
            try
            {
                Task.Run(() => OpusTranslationModels.DownloadAsync(
                    e.Args[opusDownloadIndex + 1], null, CancellationToken.None))
                    .GetAwaiter().GetResult();
                File.WriteAllText(e.Args[opusDownloadIndex + 2], "OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[opusDownloadIndex + 2], "ERROR " + ex);
                Environment.Exit(3);
            }
        }

        int opusTestIndex = Array.FindIndex(e.Args, a => a.Equals("--opus-test", StringComparison.OrdinalIgnoreCase));
        if (opusTestIndex >= 0 && opusTestIndex + 4 < e.Args.Length)
        {
            try
            {
                string translated = new OpusMtEngine().Translate(
                    e.Args[opusTestIndex + 3],
                    e.Args[opusTestIndex + 1],
                    e.Args[opusTestIndex + 2],
                    CancellationToken.None);
                File.WriteAllText(e.Args[opusTestIndex + 4], translated);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[opusTestIndex + 4], "ERROR " + ex);
                Environment.Exit(3);
            }
        }

        Ruse.App.Diagnostics.AppLog.Initialize();

        int translationE2EIndex = Array.FindIndex(e.Args, a => a.Equals("--translation-e2e-test", StringComparison.OrdinalIgnoreCase));
        if (translationE2EIndex >= 0 && translationE2EIndex + 5 < e.Args.Length)
        {
            try
            {
                string line = TranslationPipelineTest.Run(
                    e.Args[translationE2EIndex + 1],
                    e.Args[translationE2EIndex + 2],
                    e.Args[translationE2EIndex + 3],
                    e.Args[translationE2EIndex + 4],
                    180,
                    e.Args.Contains("--opus", StringComparer.OrdinalIgnoreCase) ? "opus" : "qwen");
                File.WriteAllText(e.Args[translationE2EIndex + 5], line);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[translationE2EIndex + 5], "FAILED: " + ex);
                Environment.Exit(3);
            }
        }

        int vst3TestIndex = Array.FindIndex(e.Args, a => a.Equals("--vst3-test", StringComparison.OrdinalIgnoreCase));
        if (vst3TestIndex >= 0 && vst3TestIndex + 2 < e.Args.Length)
        {
            try
            {
                string line = Vst3SelfTest.RunPlugin(e.Args[vst3TestIndex + 1]);
                File.WriteAllText(e.Args[vst3TestIndex + 2], line);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[vst3TestIndex + 2], "FAILED: " + ex);
                Environment.Exit(3);
            }
        }

        int vst3IsolatedIndex = Array.FindIndex(e.Args, a => a.Equals("--vst3-isolated-test", StringComparison.OrdinalIgnoreCase));
        if (vst3IsolatedIndex >= 0 && vst3IsolatedIndex + 2 < e.Args.Length)
        {
            try
            {
                string line = Vst3SelfTest.RunIsolated(e.Args[vst3IsolatedIndex + 1]);
                File.WriteAllText(e.Args[vst3IsolatedIndex + 2], line);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[vst3IsolatedIndex + 2], "FAILED: " + ex);
                Environment.Exit(3);
            }
        }

        int vst3RouteIndex = Array.FindIndex(e.Args, a => a.Equals("--vst3-route-test", StringComparison.OrdinalIgnoreCase));
        if (vst3RouteIndex >= 0 && vst3RouteIndex + 2 < e.Args.Length)
        {
            try
            {
                string line = Vst3SelfTest.RunRoute(e.Args[vst3RouteIndex + 1]);
                File.WriteAllText(e.Args[vst3RouteIndex + 2], line);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[vst3RouteIndex + 2], "FAILED: " + ex);
                Environment.Exit(3);
            }
        }

        int vst3EditorIndex = Array.FindIndex(e.Args, a => a.Equals("--vst3-editor-test", StringComparison.OrdinalIgnoreCase));
        if (vst3EditorIndex >= 0 && vst3EditorIndex + 2 < e.Args.Length)
        {
            try
            {
                string line = Vst3SelfTest.RunEditor(e.Args[vst3EditorIndex + 1]);
                File.WriteAllText(e.Args[vst3EditorIndex + 2], line);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(e.Args[vst3EditorIndex + 2], "FAILED: " + ex);
                Environment.Exit(3);
            }
        }

        if (e.Args.Contains("--vst3-scan-test", StringComparer.OrdinalIgnoreCase))
        {
            string output = Path.Combine(AppContext.BaseDirectory, "vst3-scan-test.txt");
            try
            {
                var found = Task.Run(() => OwnVST3Host.PluginScanner.ScanAsync(
                    OwnVST3Host.PluginFormat.Vst3,
                    OwnVST3Host.ScanMode.Fast)).GetAwaiter().GetResult();
                File.WriteAllLines(output, found.Select(plugin =>
                    $"{plugin.Name} | {plugin.Vendor} | instrument={plugin.IsInstrument} | {plugin.Identifier}"));
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(output, "FAILED: " + ex);
                Environment.Exit(3);
            }
        }

        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            DemoMode = true;
        }

        if (e.Args.Contains("--chain-test", StringComparer.OrdinalIgnoreCase))
        {
            string chainLine;
            try
            {
                chainLine = $"Ruse chain test: {LatencySelfTest.TestChainProcessor()}";
            }
            catch (Exception ex)
            {
                chainLine = $"Ruse chain test FAILED: {ex}";
            }

            WriteResult("chain-test.txt", chainLine);
            Shutdown();
            return;
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

        if (e.Args.Contains("--fanout-test", StringComparer.OrdinalIgnoreCase))
        {
            string fanLine;
            try
            {
                fanLine = $"Ruse fan-out test: {LatencySelfTest.TestFanOut()}";
            }
            catch (Exception ex)
            {
                fanLine = $"Ruse fan-out test FAILED: {ex}";
            }

            WriteResult("fanout-test.txt", fanLine);
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

        if (e.Args.Contains("--sessions", StringComparer.OrdinalIgnoreCase))
        {
            string sessionLine;
            try
            {
                var sessions = Audio.AudioSessionEnumerator.GetRenderSessions();
                sessionLine = "Ruse sessions: " + string.Join(" | ", sessions.Select(s =>
                    $"{s.Label} pid={s.ProcessId}"));
            }
            catch (Exception ex)
            {
                sessionLine = $"Ruse sessions FAILED: {ex}";
            }

            WriteResult("sessions.txt", sessionLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--delay-test", StringComparer.OrdinalIgnoreCase))
        {
            uint delayPid = (uint)GetIntArg(e.Args, "--pid", 0);
            double delaySeconds = GetDoubleArg(e.Args, "--seconds", 6.0);
            string delayLine;
            try
            {
                delayLine = $"Ruse delay test: {LatencySelfTest.TestEndToEndDelay(delayPid, delaySeconds)}";
            }
            catch (Exception ex)
            {
                delayLine = $"Ruse delay test FAILED: {ex}";
            }

            WriteResult("delay-test.txt", delayLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--latency-test", StringComparer.OrdinalIgnoreCase))
        {
            uint latPid = (uint)GetIntArg(e.Args, "--pid", 0);
            double latSeconds = GetDoubleArg(e.Args, "--seconds", 6.0);
            string latLine;
            try
            {
                latLine = $"Ruse latency test: {LatencySelfTest.TestLatencyProfile(latPid, latSeconds)}";
            }
            catch (Exception ex)
            {
                latLine = $"Ruse latency test FAILED: {ex}";
            }

            WriteResult("latency-test.txt", latLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--asio-test", StringComparer.OrdinalIgnoreCase))
        {
            string asioLine;
            try
            {
                asioLine = $"Ruse asio test: {LatencySelfTest.TestAsioDevices()}";
            }
            catch (Exception ex)
            {
                asioLine = $"Ruse asio test FAILED: {ex}";
            }

            WriteResult("asio-test.txt", asioLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--record-test", StringComparer.OrdinalIgnoreCase))
        {
            string recordLine;
            try
            {
                recordLine = $"Ruse recorder test: {LatencySelfTest.TestFileRecorder()}";
            }
            catch (Exception ex)
            {
                recordLine = $"Ruse recorder test FAILED: {ex}";
            }

            WriteResult("record-test.txt", recordLine);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--live-demo", StringComparer.OrdinalIgnoreCase))
        {
            // Carries real audio so the UI (meters) can be checked against a
            // genuine signal. The window stays open.
            uint livePid = (uint)GetIntArg(e.Args, "--pid", 0);
            LiveDemoMode = true;
            LiveDemoProcessId = livePid;
            base.OnStartup(e);
            var webWindow = new WebMainWindow();
            MainWindow = webWindow;
            webWindow.Show();
            return;
        }

        if (e.Args.Contains("--web-test", StringComparer.OrdinalIgnoreCase))
        {
            string testDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Ruse-webtest");
            try
            {
                if (System.IO.Directory.Exists(testDirectory))
                {
                    System.IO.Directory.Delete(testDirectory, recursive: true);
                }
            }
            catch
            {
            }

            AppSettings.OverrideDirectory = testDirectory;
            string result;
            try
            {
                var window = new WebMainWindow();
                result = window.RunParitySelfTest();
                window.Close();
            }
            catch (Exception ex)
            {
                result = "FAILED: " + ex;
            }

            WriteResult("web-test.txt", $"Ruse web test: {result}");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var mainWindow = new WebMainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    /// <summary>
    /// Loads a model in a disposable child process and writes the outcome to
    /// <paramref name="resultPath"/>. A native Whisper crash kills this
    /// process, not the app that is checking the model.
    /// </summary>
    private static void RunModelProbe(string modelPath, string resultPath)
    {
        try
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            using var processor = factory.CreateBuilder().WithLanguage("en").Build();
            processor.Process(new float[16000]);
            long peak = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
            File.WriteAllText(resultPath, $"OK {peak / (1024 * 1024)}");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(resultPath, $"ERROR {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }

            Environment.Exit(3);
        }
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

