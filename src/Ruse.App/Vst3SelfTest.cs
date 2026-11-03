using Ruse.App.Audio;

namespace Ruse.App;

internal static class Vst3SelfTest
{
    public static string RunPlugin(string identifier, int timeoutSeconds = 60)
    {
        using var processor = new Vst3Processor(identifier);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!processor.IsReady && processor.Error == null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!processor.IsReady)
        {
            return $"FAILED: {processor.Error ?? "timed out waiting for the plugin"}";
        }

        var block = new float[480 * 2];
        double rms = 0;
        for (int pass = 0; pass < 10; pass++)
        {
            for (int frame = 0; frame < 480; frame++)
            {
                float sample = 0.2f * MathF.Sin(2f * MathF.PI * 440f * (pass * 480 + frame) / 48000f);
                block[frame * 2] = sample;
                block[frame * 2 + 1] = sample;
            }

            processor.Process(block, 480);
            if (pass == 9)
            {
                for (int i = 0; i < block.Length; i++)
                {
                    rms += block[i] * block[i];
                }
            }
        }

        rms = Math.Sqrt(rms / block.Length);
        string state = processor.CaptureStateBase64() is { Length: > 0 } saved ? $"{saved.Length} chars" : "none";

        var preset = PresetSerializer.SerializeProcessor(processor);
        string restore = "skipped";
        using (var restored = PresetSerializer.CreateProcessor(preset) as IVst3Processor)
        {
            if (restored != null)
            {
                DateTime restoreDeadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (!restored.IsReady && restored.Error == null && DateTime.UtcNow < restoreDeadline)
                {
                    Thread.Sleep(50);
                }

                restore = restored.IsReady
                    ? $"{restored.PluginName} ({restored.Parameters.Count} params)"
                    : $"failed: {restored.Error}";
            }
        }

        return $"name={processor.PluginName}; vendor={processor.Vendor}; version={processor.Version}; " +
               $"parameters={processor.Parameters.Count}; latency={processor.LatencySamples} frames; " +
               $"editor={processor.HasEditor}; rms={rms:0.0000}; state={state}; restore={restore}";
    }

    public static string RunIsolated(string identifier, int timeoutSeconds = 60)
    {
        using var processor = new IsolatedVst3Processor(identifier);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!processor.IsReady && processor.Error == null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!processor.IsReady)
        {
            return $"FAILED: {processor.Error ?? "timed out waiting for the isolated plugin"}";
        }

        var block = new float[480 * 2];
        double rms = 0;
        for (int pass = 0; pass < 10; pass++)
        {
            for (int frame = 0; frame < 480; frame++)
            {
                float sample = 0.2f * MathF.Sin(2f * MathF.PI * 440f * (pass * 480 + frame) / 48000f);
                block[frame * 2] = sample;
                block[frame * 2 + 1] = sample;
            }

            processor.Process(block, 480);
            if (pass == 9)
            {
                for (int i = 0; i < block.Length; i++) rms += block[i] * block[i];
                rms = Math.Sqrt(rms / block.Length);
            }
        }
        string state = processor.CaptureStateBase64() is { Length: > 0 } saved ? $"{saved.Length} chars" : "none";
        return $"name={processor.PluginName}; vendor={processor.Vendor}; version={processor.Version}; " +
               $"parameters={processor.Parameters.Count}; latency={processor.LatencySamples} frames; " +
               $"editor={processor.HasEditor}; rms={rms:0.0000}; state={state}";
    }

    public static string Benchmark(string identifier, int blocks = 2000, int warmup = 200,
        int timeoutSeconds = 60)
    {
        blocks = Math.Clamp(blocks, 100, 20000);
        warmup = Math.Clamp(warmup, 10, 5000);
        var isolated = Measure(new IsolatedVst3Processor(identifier), blocks, warmup, timeoutSeconds);
        var inProcess = Measure(new Vst3Processor(identifier), blocks, warmup, timeoutSeconds);
        return $"isolated: {Format(isolated)} | in-process: {Format(inProcess)} | " +
               $"ipc overhead: {isolated.Mean - inProcess.Mean:0.0} us/block | " +
               $"blocks={blocks}, frames=240, budget=5000 us";
    }

    private static Measurement Measure(IVst3Processor processor, int blocks, int warmup,
        int timeoutSeconds)
    {
        using (processor)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!processor.IsReady && processor.Error == null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }

            if (!processor.IsReady)
            {
                throw new InvalidOperationException(
                    $"Plugin did not become ready: {processor.Error ?? "timeout"}");
            }

            const int frames = 240;
            var block = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                float sample = 0.2f * MathF.Sin(2f * MathF.PI * 440f * frame / 48000f);
                block[frame * 2] = sample;
                block[frame * 2 + 1] = sample;
            }

            for (int i = 0; i < warmup; i++)
            {
                processor.Process(block, frames);
            }

            var samples = new double[blocks];
            for (int i = 0; i < blocks; i++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                processor.Process(block, frames);
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                samples[i] = (end - start) * 1_000_000.0 /
                             System.Diagnostics.Stopwatch.Frequency;
            }

            Array.Sort(samples);
            double mean = samples.Average();
            double p50 = Percentile(samples, 0.50);
            double p95 = Percentile(samples, 0.95);
            double p99 = Percentile(samples, 0.99);
            return new Measurement(mean, p50, p95, p99, samples[^1]);
        }
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(sorted.Length - 1, lower + 1);
        double fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static string Format(Measurement measurement) =>
        $"avg={measurement.Mean:0.0}us p50={measurement.P50:0.0}us " +
        $"p95={measurement.P95:0.0}us p99={measurement.P99:0.0}us max={measurement.Max:0.0}us";

    private readonly record struct Measurement(double Mean, double P50, double P95,
        double P99, double Max);

    public static string RunEditor(string identifier, int timeoutSeconds = 60)
    {
        using var vst = new Vst3Processor(identifier);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!vst.IsReady && vst.Error == null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!vst.IsReady)
        {
            return $"FAILED: {vst.Error ?? "timed out waiting for the plugin"}";
        }

        if (!vst.HasEditor)
        {
            return "FAILED: plugin has no editor";
        }

        bool opened = vst.OpenEditor("VST3 editor test");
        Thread.Sleep(1500);
        bool stillOpen = vst.IsEditorOpen;
        vst.CloseEditor();
        return $"opened={opened}; wasOpen={stillOpen}; closed={!vst.IsEditorOpen}";
    }

    public static string RunRoute(string identifier, int timeoutSeconds = 60)
    {
        using var vst = new Vst3Processor(identifier);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!vst.IsReady && vst.Error == null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!vst.IsReady)
        {
            return $"FAILED: {vst.Error ?? "timed out waiting for the plugin"}";
        }

        float peak = 0;
        var graph = new AudioGraph { AutoRun = false };
        using var source = new SourceNode("VST route test", SourceKind.Application, 0);
        var modifier = new ModifierNode("VST route test");
        modifier.Add(vst);
        graph.AddSource(source);
        graph.AddModifier(modifier);
        var output = graph.AddOutputForTest((mix, length) =>
        {
            for (int i = 0; i < length; i++)
            {
                peak = Math.Max(peak, Math.Abs(mix[i]));
            }
        });
        output.PaceRealTime = true;
        graph.ConnectSourceToModifier(source, modifier);
        graph.ConnectModifierToOutput(modifier, output);
        output.Start();
        try
        {
            for (int block = 0; block < 30; block++)
            {
                var samples = new float[240 * 2];
                for (int frame = 0; frame < 240; frame++)
                {
                    float sample = 0.2f * MathF.Sin(2f * MathF.PI * 440f * (block * 240 + frame) / 48000f);
                    samples[frame * 2] = sample;
                    samples[frame * 2 + 1] = sample;
                }

                source.WriteTestSamples(samples);
                Thread.Sleep(5);
            }

            Thread.Sleep(120);
        }
        finally
        {
            output.Stop();
            graph.Dispose();
        }

        return peak > 0.01f
            ? $"route=PASS peak={peak:0.0000} latency={output.ChainLatencyMs:0.0} ms"
            : $"route=FAILED peak={peak:0.0000}";
    }
}
