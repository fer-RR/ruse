using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>Offline measurements for a file-player source. Runs off the UI thread.</summary>
public sealed class AudioFileAnalysis
{
    public string FileName { get; init; } = "";
    public string Extension { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public double DurationSeconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public string Encoding { get; init; } = "";
    public double BitrateKbps { get; init; }
    public long FrameCount { get; init; }
    public float PeakDb { get; init; }
    public float TruePeakDb { get; init; }
    public float RmsDb { get; init; }
    public float CrestFactorDb { get; init; }
    public float IntegratedLufs { get; init; }
    public float LoudnessRange { get; init; }
    public float DcOffsetLeft { get; init; }
    public float DcOffsetRight { get; init; }
    public float StereoCorrelation { get; init; }
    public float StereoBalanceDb { get; init; }
    public double SilencePercent { get; init; }
    public long ClippedSamples { get; init; }
    public DateTime AnalyzedUtc { get; init; }

    public static AudioFileAnalysis Analyze(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        using var reader = new AudioFileReader(path);
        WaveFormat format = reader.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int sampleRate = Math.Max(1, format.SampleRate);
        double duration = reader.TotalTime.TotalSeconds;

        var converter = new FormatConverter(channels, sampleRate, FormatConverter.TargetSampleRate);
        var loudness = new LoudnessTap();
        var source = new float[65536];
        var converted = new List<float>(65536 * 2);
        var span = CollectionsMarshal.AsSpan(converted);

        double sumL = 0;
        double sumR = 0;
        double sumSqL = 0;
        double sumSqR = 0;
        double sumLR = 0;
        float peak = 0;
        long clipped = 0;
        long silent = 0;
        long frames = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = ((ISampleProvider)reader).Read(source.AsSpan());
            if (read <= 0)
            {
                break;
            }

            converted.Clear();
            converter.Convert(source.AsSpan(0, read), converted);
            span = CollectionsMarshal.AsSpan(converted);
            int blockFrames = span.Length / FormatConverter.TargetChannels;
            if (blockFrames <= 0)
            {
                continue;
            }

            loudness.Write(span, blockFrames);
            for (int frame = 0; frame < blockFrames; frame++)
            {
                float left = span[frame * 2];
                float right = span[(frame * 2) + 1];
                float magnitude = Math.Max(Math.Abs(left), Math.Abs(right));
                if (magnitude > peak) peak = magnitude;
                if (magnitude >= 0.999f) clipped++;
                if (magnitude < 1e-4f) silent++;

                sumL += left;
                sumR += right;
                sumSqL += (double)left * left;
                sumSqR += (double)right * right;
                sumLR += (double)left * right;
                frames++;
            }
        }

        double rms = frames > 0
            ? Math.Sqrt((sumSqL + sumSqR) / (2.0 * frames))
            : 0;
        double denominator = Math.Sqrt(sumSqL * sumSqR);
        float correlation = denominator > 1e-12
            ? (float)Math.Clamp(sumLR / denominator, -1.0, 1.0)
            : 0f;
        double rmsL = frames > 0 ? Math.Sqrt(sumSqL / frames) : 0;
        double rmsR = frames > 0 ? Math.Sqrt(sumSqR / frames) : 0;
        float balance = rmsL > 1e-12 && rmsR > 1e-12
            ? (float)Math.Clamp(20.0 * Math.Log10(rmsR / rmsL), -60.0, 60.0)
            : 0f;
        float peakDb = ToDb(peak);
        float rmsDb = ToDb(rms);

        return new AudioFileAnalysis
        {
            FileName = file.Name,
            Extension = file.Extension.TrimStart('.').ToUpperInvariant(),
            FileSizeBytes = file.Exists ? file.Length : 0,
            LastModifiedUtc = file.Exists ? file.LastWriteTimeUtc : null,
            DurationSeconds = duration,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = format.BitsPerSample,
            Encoding = format.Encoding.ToString(),
            BitrateKbps = Math.Round(format.AverageBytesPerSecond * 8.0 / 1000.0, 1),
            FrameCount = frames,
            PeakDb = peakDb,
            TruePeakDb = ClampDb(loudness.TruePeakDb),
            RmsDb = rmsDb,
            CrestFactorDb = Math.Max(0f, peakDb - rmsDb),
            IntegratedLufs = ClampDb(loudness.IntegratedLufs),
            LoudnessRange = Math.Max(0f, loudness.LoudnessRange),
            DcOffsetLeft = frames > 0 ? (float)(sumL / frames) : 0f,
            DcOffsetRight = frames > 0 ? (float)(sumR / frames) : 0f,
            StereoCorrelation = correlation,
            StereoBalanceDb = balance,
            SilencePercent = frames > 0 ? (100.0 * silent / frames) : 0,
            ClippedSamples = clipped,
            AnalyzedUtc = DateTime.UtcNow,
        };
    }

    private static float ToDb(double value) =>
        value <= 1e-12 ? -120f : (float)(20.0 * Math.Log10(value));

    private static float ClampDb(float value) =>
        float.IsFinite(value) ? Math.Max(-120f, Math.Min(0f, value)) : -120f;
}
