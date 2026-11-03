using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Ruse.App.Audio;

namespace Ruse.App;

/// <summary>End-to-end test through a VB-Audio Cable with effects and rnnoise.</summary>
internal static class CableSelfTest
{
    public static string Run(string inputWav, string outputWet, double seconds = 8,
        string renderMatch = "CABLE Input", string captureMatch = "CABLE Output")
    {
        seconds = Math.Clamp(seconds, 2.0, 30.0);

        if (!File.Exists(inputWav))
        {
            return $"FAILED: input file not found: {inputWav}";
        }

        string rnnoisePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Common", "VST3", "rnnoise.vst3");
        if (!Directory.Exists(rnnoisePath))
        {
            return $"FAILED: rnnoise.vst3 not found in {rnnoisePath}";
        }

        using var enumerator = new MMDeviceEnumerator();
        var renderDevice = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(device => device.FriendlyName.Contains(renderMatch, StringComparison.OrdinalIgnoreCase));
        var captureDevice = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(device => device.FriendlyName.Contains(captureMatch, StringComparison.OrdinalIgnoreCase));
        if (renderDevice == null || captureDevice == null)
        {
            return $"FAILED: could not find '{renderMatch}' and '{captureMatch}' devices.";
        }

        string driverName = renderDevice.FriendlyName;

        string outputDry = Path.Combine(
            Path.GetDirectoryName(outputWet) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(outputWet) + "-dry.wav");
        string outputEffects = Path.Combine(
            Path.GetDirectoryName(outputWet) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(outputWet) + "-effects.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(outputWet) ?? Path.GetTempPath());
        Directory.CreateDirectory(Path.GetDirectoryName(outputDry) ?? Path.GetTempPath());

        string marked = Path.Combine(Path.GetTempPath(),
            "ruse-cable-" + Guid.NewGuid().ToString("N") + ".wav");
        CreateMarkedFile(inputWav, marked);
        string? fault = null;
        var captureLock = new object();
        double firstSoundMs = -1;
        double dryFirstSoundMs = -1;
        bool fileStarted = false;
        var clock = new Stopwatch();

        using var graph = new AudioGraph { AutoRun = false };
        var fileSource = new SourceNode("Cable test input", SourceKind.File, filePath: marked, fileLoop: false);
        var captureSource = new SourceNode("CABLE Output", SourceKind.Device, deviceId: captureDevice.ID);
        fileSource.Faulted += (_, reason) => fault ??= "input: " + reason;
        captureSource.Faulted += (_, reason) => fault ??= "capture: " + reason;
        graph.AddSource(fileSource);
        graph.AddSource(captureSource);

        var cableOutput = graph.AddOutput(renderDevice);
        graph.Connect(fileSource, cableOutput);

        IVst3Processor? rnnoise = null;
        var wetModifier = new ModifierNode("CABLE rnnoise + effects");
        var effectsModifier = new ModifierNode("CABLE effects only");
        try
        {
            rnnoise = Vst3ProcessorFactory.Create(rnnoisePath);
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!rnnoise.IsReady && rnnoise.Error == null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }

            if (!rnnoise.IsReady)
            {
                return $"FAILED: rnnoise did not load: {rnnoise.Error ?? "timeout"}";
            }

            string rnnoiseParameters = string.Join(",",
                rnnoise.Parameters.Select(parameter => $"{parameter.Name}={parameter.Get():0.###}"));
            wetModifier.Add(rnnoise);
            AddEffects(wetModifier);
            AddEffects(effectsModifier);

            graph.AddModifier(wetModifier);
            graph.AddModifier(effectsModifier);
            graph.ConnectSourceToModifier(captureSource, wetModifier);
            graph.ConnectSourceToModifier(captureSource, effectsModifier);

            var dryRecorder = graph.AddOutputForFile(outputDry, AudioFileFormat.Wav24);
            dryRecorder.RecorderMode = RecorderMode.On;
            graph.Connect(captureSource, dryRecorder);

            var effectsRecorder = graph.AddOutputForFile(outputEffects, AudioFileFormat.Wav24);
            effectsRecorder.RecorderMode = RecorderMode.On;
            graph.ConnectModifierToOutput(effectsModifier, effectsRecorder);

            var wetRecorder = graph.AddOutputForFile(outputWet, AudioFileFormat.Wav24);
            wetRecorder.RecorderMode = RecorderMode.On;
            graph.ConnectModifierToOutput(wetModifier, wetRecorder);

            var monitor = graph.AddOutputForTest((mix, length) =>
            {
                lock (captureLock)
                {
                    if (fileStarted && firstSoundMs < 0)
                    {
                        for (int i = 0; i < length; i++)
                        {
                            if (Math.Abs(mix[i]) > 0.02f)
                            {
                                firstSoundMs = clock.Elapsed.TotalMilliseconds;
                                break;
                            }
                        }
                    }
                }
            });
            monitor.PaceRealTime = true;
            graph.ConnectModifierToOutput(wetModifier, monitor);

            var dryMonitor = graph.AddOutputForTest((mix, length) =>
            {
                lock (captureLock)
                {
                    if (fileStarted && dryFirstSoundMs < 0)
                    {
                        for (int i = 0; i < length; i++)
                        {
                            if (Math.Abs(mix[i]) > 0.02f)
                            {
                                dryFirstSoundMs = clock.Elapsed.TotalMilliseconds;
                                break;
                            }
                        }
                    }
                }
            });
            dryMonitor.PaceRealTime = true;
            graph.Connect(captureSource, dryMonitor);

            graph.StartEngine();
            cableOutput.Start();
            dryRecorder.Start();
            effectsRecorder.Start();
            wetRecorder.Start();
            monitor.Start();
            dryMonitor.Start();
            captureSource.Start();
            Thread.Sleep(500);

            var process = Process.GetCurrentProcess();
            TimeSpan cpuStart = process.TotalProcessorTime;
            long gcStart = GC.GetTotalAllocatedBytes(precise: true);
            clock.Restart();
            fileStarted = true;
            fileSource.Start();

            double waitSeconds = Math.Min(seconds, Math.Max(2.0, fileSource.FileDurationSeconds + 1.0));
            while (clock.Elapsed.TotalSeconds < waitSeconds && fileSource.IsRunning && fault == null)
            {
                Thread.Sleep(50);
            }

            Thread.Sleep(400);
            double elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            TimeSpan cpu = process.TotalProcessorTime - cpuStart;
            long gcBytes = GC.GetTotalAllocatedBytes(precise: true) - gcStart;
            long capturedFrames = captureSource.CapturedFrames;
            double wetChainLatency = wetRecorder.ChainLatencyMs;
            long captureAllocated = captureSource.CaptureAllocatedBytes;
            long fileAllocated = fileSource.CaptureAllocatedBytes;

            fileSource.Stop();
            captureSource.Stop();
            cableOutput.Stop();
            dryRecorder.Stop();
            effectsRecorder.Stop();
            wetRecorder.Stop();
            monitor.Stop();
            dryMonitor.Stop();
            graph.StopEngine();

            var (wetPeak, wetRms, wetClipped) = Analyze(outputWet);
            var (dryPeak, dryRms, _) = Analyze(outputDry);
            var (effectsPeak, effectsRms, _) = Analyze(outputEffects);
            double cpuPercent = cpu.TotalMilliseconds / (elapsed * 1000.0) /
                                Math.Max(1, Environment.ProcessorCount) * 100.0;
            double allocKbPerSecond = gcBytes / 1024.0 / elapsed;
            long shortReads = monitor.ShortReadFrames + dryMonitor.ShortReadFrames +
                              cableOutput.ShortReadFrames;

            string latency = firstSoundMs >= 0
                ? $"{firstSoundMs:0.0} ms to first marker"
                : "marker not detected";
            string dryLatency = dryFirstSoundMs >= 0
                ? $"{dryFirstSoundMs:0.0} ms"
                : "not detected";
            string summary =
                $"CABLE loop: render='{driverName}', capture='{captureDevice.FriendlyName}'; " +
                $"rnnoise latency={rnnoise.LatencySamples} frames [{rnnoiseParameters}]; " +
                $"capture low-latency={captureSource.CaptureLowLatencyActive}; " +
                $"latency={latency} (dry cable={dryLatency}); " +
                $"cable render latency={cableOutput.EstimatedLatencyMs:0.0} ms, " +
                $"wet chain={wetChainLatency:0.0} ms; " +
                $"captured={capturedFrames} frames; " +
                $"wet peak={wetPeak:0.000} rms={wetRms:0.000} clipped={wetClipped}; " +
                $"dry peak={dryPeak:0.000} rms={dryRms:0.000}; " +
                $"effects peak={effectsPeak:0.000} rms={effectsRms:0.000}; " +
                $"capture alloc={captureAllocated} B, file alloc={fileAllocated} B; " +
                $"short reads={shortReads}; CPU={cpuPercent:0.0}%; GC={allocKbPerSecond:0.0} KiB/s; " +
                $"wet={outputWet}; dry={outputDry}; effects={outputEffects}" +
                (fault == null ? "." : $" | FAULT: {fault}");

            return summary;
        }
        finally
        {
            TryDelete(marked);
        }
    }

    private static void CreateMarkedFile(string source, string target)
    {
        using var reader = new AudioFileReader(source);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(
            reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        using var writer = new WaveFileWriter(target, format);
        int channels = format.Channels;
        int sampleRate = format.SampleRate;

        var marker = new float[(int)(sampleRate * 0.15) * channels];
        for (int frame = 0; frame < marker.Length / channels; frame++)
        {
            float value = 0.7f * MathF.Sin(2f * MathF.PI * 1000f * frame / sampleRate);
            marker[frame * channels] = value;
            if (channels > 1)
            {
                marker[frame * channels + 1] = value;
            }
        }

        writer.WriteSamples(marker, 0, marker.Length);
        var silence = new float[(int)(sampleRate * 0.25) * channels];
        writer.WriteSamples(silence, 0, silence.Length);

        var buffer = new float[sampleRate * channels];
        int read;
        while ((read = reader.Read(buffer.AsSpan(0, buffer.Length))) > 0)
        {
            writer.WriteSamples(buffer, 0, read);
        }
    }

    private static void AddEffects(ModifierNode modifier)
    {
        if (AudioProcessorCatalog.Create("highpass") is { } highPass)
        {
            modifier.Add(highPass);
        }

        if (AudioProcessorCatalog.Create("compressor") is { } compressor)
        {
            modifier.Add(compressor);
        }

        if (AudioProcessorCatalog.Create("limiter") is { } limiter)
        {
            modifier.Add(limiter);
        }
    }

    private static (double Peak, double Rms, long Clipped) Analyze(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            var buffer = new float[8192];
            double sum = 0;
            double peak = 0;
            long clipped = 0;
            long count = 0;
            int read;
            while ((read = reader.Read(buffer.AsSpan(0, buffer.Length))) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    double value = buffer[i];
                    sum += value * value;
                    double absolute = Math.Abs(value);
                    if (absolute > peak) peak = absolute;
                    if (absolute >= 0.999) clipped++;
                    count++;
                }
            }

            return (peak, count > 0 ? Math.Sqrt(sum / count) : 0, clipped);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
