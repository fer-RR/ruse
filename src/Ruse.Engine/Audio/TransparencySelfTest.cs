using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Offline transparency checks for the converter and the unity routing path.
/// This is intentionally not used on the realtime path. It writes WAVs for an
/// optional external ViSQOL run.
/// </summary>
public static class TransparencySelfTest
{
    private const int Channels = 2;
    private const int TargetRate = 48000;

    public sealed record Result(bool Passed, string Summary, string Report, string ReferencePath, string ProcessedPath);

    public static Result Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var report = new List<string>();
        var failures = new List<string>();

        double identityNull = MeasureIdentityNull();
        report.Add($"identity null: {identityNull:0.0} dBFS");
        if (identityNull > -100)
        {
            failures.Add("unity path null residual");
        }

        var src441 = MeasureSource(44100, 1_000);
        report.Add($"44.1k -> 48k: gain {src441.GainDb:0.00} dB, THD+N {src441.ThdnDb:0.0} dB, alias {src441.AliasDb:0.0} dBFS");
        if (Math.Abs(src441.GainDb) > 0.2 || src441.ThdnDb > -70)
        {
            failures.Add("44.1k converter");
        }

        var src96 = MeasureSource(96000, 1_000);
        report.Add($"96k -> 48k: gain {src96.GainDb:0.00} dB, THD+N {src96.ThdnDb:0.0} dB, alias {src96.AliasDb:0.0} dBFS");
        if (Math.Abs(src96.GainDb) > 0.2 || src96.ThdnDb > -70 || src96.AliasDb > -60)
        {
            failures.Add("96k converter");
        }

        double centerGain = MeasureCenterDownmix();
        report.Add($"5.1 center downmix: {centerGain:0.000} on each side");
        if (Math.Abs(centerGain - 0.70710678 * 0.5) > 0.005)
        {
            failures.Add("5.1 center downmix");
        }

        var referencePath = Path.Combine(outputDirectory, "transparency-reference.wav");
        var processedPath = Path.Combine(outputDirectory, "transparency-processed.wav");
        WriteVisqolPair(referencePath, processedPath);
        report.Add($"ViSQOL pair: {referencePath}");
        report.Add($"ViSQOL pair: {processedPath}");

        bool passed = failures.Count == 0;
        string summary = passed
            ? "transparency: identity null, 44.1/96k SRC, 5.1 downmix pass"
            : "transparency FAILED: " + string.Join("; ", failures);
        return new Result(passed, summary, string.Join(Environment.NewLine, report), referencePath, processedPath);
    }

    private static double MeasureIdentityNull()
    {
        const int frames = TargetRate;
        var input = GenerateTone(TargetRate, frames, 1_000, 0.25);
        var output = new List<float>(input.Length);
        bool previousGuard = OutputNode.ClipGuardEnabled;
        OutputNode.ClipGuardEnabled = false;
        try
        {
            var graph = new AudioGraph { AutoRun = false };
            var source = new SourceNode("transparency", SourceKind.Application);
            graph.AddSource(source);
            var sink = graph.AddOutputForTest((mix, length) =>
            {
                for (int i = 0; i < length; i++)
                {
                    output.Add(mix[i]);
                }
            });
            graph.Connect(source, sink);

            var mix = new float[240 * Channels];
            var temp = new float[240 * Channels];
            var bytes = new byte[mix.Length * sizeof(float)];
            for (int offset = 0; offset + 240 <= frames; offset += 240)
            {
                source.WriteTestSamples(input.AsSpan(offset * Channels, 240 * Channels));
                sink.RenderChunk(mix, temp, bytes, Channels);
            }

            graph.Dispose();
        }
        finally
        {
            OutputNode.ClipGuardEnabled = previousGuard;
        }

        double sum = 0;
        double reference = 0;
        int count = Math.Min(input.Length, output.Count);
        for (int i = 0; i < count; i++)
        {
            double d = output[i] - input[i];
            sum += d * d;
            reference += input[i] * input[i];
        }

        return 10 * Math.Log10(Math.Max(1e-20, sum) / Math.Max(1e-20, reference));
    }

    private sealed record SourceResult(double GainDb, double ThdnDb, double AliasDb);

    private static SourceResult MeasureSource(int sourceRate, double frequency)
    {
        const int seconds = 2;
        int inputFrames = sourceRate * seconds;
        var input = GenerateTone(sourceRate, inputFrames, frequency, 0.25);
        var converter = new FormatConverter(Channels, sourceRate);
        var output = new List<float>(TargetRate * seconds * Channels);
        converter.Convert(input, output);

        int outFrames = output.Count / Channels;
        int start = 128;
        int analyzeFrames = Math.Min(TargetRate / 2, Math.Max(0, outFrames - start - 64));
        var (gain, thdn) = AnalyzeTone(output, start, analyzeFrames, frequency);
        double gainDb = 20 * Math.Log10(Math.Max(1e-12, gain / 0.25));

        double aliasDb = -180;
        if (sourceRate > TargetRate)
        {
            var aliasInput = GenerateTone(sourceRate, sourceRate / 2, 30_000, 0.25);
            var aliasOutput = new List<float>();
            converter = new FormatConverter(Channels, sourceRate);
            converter.Convert(aliasInput, aliasOutput);
            double sum = 0;
            int count = Math.Max(1, aliasOutput.Count);
            for (int i = 0; i < count; i++)
            {
                sum += aliasOutput[i] * aliasOutput[i];
            }

            aliasDb = 10 * Math.Log10(Math.Max(1e-20, sum / count));
        }

        return new SourceResult(gainDb, 10 * Math.Log10(Math.Max(1e-20, thdn)), aliasDb);
    }

    private static double MeasureCenterDownmix()
    {
        var converter = new FormatConverter(6, TargetRate);
        var input = new float[6];
        input[2] = 0.5f;
        var output = new List<float>();
        converter.Convert(input, output);
        return output.Count >= 2 ? Math.Abs(output[0]) : 0;
    }

    private static (double amplitude, double residualRatio) AnalyzeTone(
        List<float> data,
        int startFrame,
        int frames,
        double frequency)
    {
        double sumSin = 0;
        double sumCos = 0;
        double sumEnergy = 0;
        for (int f = 0; f < frames; f++)
        {
            int index = (startFrame + f) * Channels;
            double sample = data[index];
            double phase = 2 * Math.PI * frequency * (startFrame + f) / TargetRate;
            sumSin += sample * Math.Sin(phase);
            sumCos += sample * Math.Cos(phase);
            sumEnergy += sample * sample;
        }

        double a = 2 * sumSin / frames;
        double b = 2 * sumCos / frames;
        double amplitude = Math.Sqrt(a * a + b * b);

        double residual = 0;
        for (int f = 0; f < frames; f++)
        {
            int index = (startFrame + f) * Channels;
            double phase = 2 * Math.PI * frequency * (startFrame + f) / TargetRate;
            double expected = a * Math.Sin(phase) + b * Math.Cos(phase);
            double d = data[index] - expected;
            residual += d * d;
        }

        return (amplitude, residual / Math.Max(1e-20, sumEnergy));
    }

    private static float[] GenerateTone(int sampleRate, int frames, double frequency, double amplitude)
    {
        var data = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            float sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * f / sampleRate));
            data[f * Channels] = sample;
            data[f * Channels + 1] = sample;
        }

        return data;
    }

    private static void WriteVisqolPair(string referencePath, string processedPath)
    {
        const int seconds = 8;
        const int sourceRate = 44100;
        var reference = new float[TargetRate * seconds * Channels];
        var source = new float[sourceRate * (seconds + 1) * Channels];
        double[] tones = { 220, 440, 1000, 2500, 6000 };
        double[] gains = { 0.12, 0.10, 0.08, 0.06, 0.04 };
        for (int f = 0; f < reference.Length / Channels; f++)
        {
            double sample = 0;
            for (int i = 0; i < tones.Length; i++)
            {
                sample += gains[i] * Math.Sin(2 * Math.PI * tones[i] * f / TargetRate);
            }

            reference[f * Channels] = (float)sample;
            reference[f * Channels + 1] = (float)sample;
        }

        for (int f = 0; f < source.Length / Channels; f++)
        {
            double sample = 0;
            for (int i = 0; i < tones.Length; i++)
            {
                sample += gains[i] * Math.Sin(2 * Math.PI * tones[i] * f / sourceRate);
            }

            source[f * Channels] = (float)sample;
            source[f * Channels + 1] = (float)sample;
        }

        var converter = new FormatConverter(Channels, sourceRate);
        var processed = new List<float>();
        converter.Convert(source, processed);
        int target = reference.Length;
        var processedFixed = new float[target];
        Array.Copy(processed.ToArray(), processedFixed, Math.Min(target, processed.Count));

        WriteWav(referencePath, reference);
        WriteWav(processedPath, processedFixed);
    }

    private static void WriteWav(string path, float[] samples)
    {
        var format = new WaveFormat(TargetRate, 24, Channels);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
