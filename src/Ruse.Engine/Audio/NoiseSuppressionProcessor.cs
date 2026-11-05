using System.Collections.Concurrent;
using System.IO;
using NAudio.Dsp;
using NAudio.Effects;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ruse.App.Audio;

/// <summary>
/// NAudio's adaptive spectral suppressor plus a static profile filter built from
/// a recorded noise-floor file. The profile is a lightweight average magnitude
/// spectrum; the Remove parameter controls how much of it is subtracted.
/// </summary>
public sealed class NoiseSuppressionProcessor : IAudioProcessor, IParameterizedProcessor, INoiseProfileProcessor
{
    public const string Id = "noise-suppression";

    private readonly NaudioEffectProcessor _inner;
    private readonly AudioParameter[] _parameters;
    private readonly float[] _historyLeft = new float[NoiseProfileAnalyzer.FirTaps];
    private readonly float[] _historyRight = new float[NoiseProfileAnalyzer.FirTaps];
    private int _historyPosition;
    private int _resetRequested;
    private float[]? _profile;
    private float[]? _kernel;
    private float _removePercent = 50f;
    private string? _noiseProfilePath;
    private string _noiseProfileName = "";
    private string? _noiseProfileError;

    public NoiseSuppressionProcessor()
    {
        _inner = new NaudioEffectProcessor(Id, "Noise suppression", new NoiseSuppressionEffect());
        var parameters = new List<AudioParameter>(_inner.Parameters)
        {
            new AudioParameter
            {
                Name = "Remove",
                Min = 0f,
                Max = 100f,
                Default = 50f,
                Unit = "%",
                IsInteger = true,
                Get = () => _removePercent,
                Set = SetRemove,
            },
        };
        _parameters = parameters.ToArray();
    }

    public string TypeId => Id;
    public string DisplayName => "Noise suppression";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;

    public int LatencySamples =>
        _inner.LatencySamples + (Volatile.Read(ref _kernel) != null ? NoiseProfileAnalyzer.FirLatency : 0);

    public string? NoiseProfilePath => _noiseProfilePath;
    public string NoiseProfileName => _noiseProfileName;
    public string? NoiseProfileError => _noiseProfileError;
    public bool HasNoiseProfile => _profile != null;

    public void LoadNoiseProfile(string path)
    {
        try
        {
            var profile = NoiseProfileAnalyzer.Analyze(path);
            _profile = profile;
            _noiseProfilePath = path;
            _noiseProfileName = Path.GetFileName(path);
            _noiseProfileError = null;
            RebuildKernel();
            Interlocked.Exchange(ref _resetRequested, 1);
        }
        catch (Exception ex)
        {
            _noiseProfileError = ex.Message;
            throw;
        }
    }

    public void ClearNoiseProfile()
    {
        _profile = null;
        Volatile.Write(ref _kernel, null);
        _noiseProfilePath = null;
        _noiseProfileName = "";
        _noiseProfileError = null;
        Interlocked.Exchange(ref _resetRequested, 1);
    }

    public void Process(Span<float> samples, int frames)
    {
        _inner.Process(samples, frames);

        var kernel = Volatile.Read(ref _kernel);
        if (kernel == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
        {
            Array.Clear(_historyLeft);
            Array.Clear(_historyRight);
            _historyPosition = 0;
        }

        int taps = kernel.Length;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * 2;
            _historyLeft[_historyPosition] = samples[index];
            _historyRight[_historyPosition] = samples[index + 1];

            float sumLeft = 0f;
            float sumRight = 0f;
            int historyIndex = _historyPosition;
            for (int tap = 0; tap < taps; tap++)
            {
                float weight = kernel[tap];
                sumLeft += _historyLeft[historyIndex] * weight;
                sumRight += _historyRight[historyIndex] * weight;
                historyIndex = historyIndex == 0 ? taps - 1 : historyIndex - 1;
            }

            samples[index] = sumLeft;
            samples[index + 1] = sumRight;
            _historyPosition = _historyPosition + 1 == taps ? 0 : _historyPosition + 1;
        }
    }

    public void Reset()
    {
        _inner.Reset();
        Interlocked.Exchange(ref _resetRequested, 1);
    }

    private void SetRemove(float value)
    {
        _removePercent = Math.Clamp(value, 0f, 100f);
        RebuildKernel();
    }

    private void RebuildKernel()
    {
        var profile = _profile;
        Volatile.Write(
            ref _kernel,
            profile == null ? null : NoiseProfileAnalyzer.BuildKernel(profile, _removePercent / 100f));
    }
}

/// <summary>
/// Lightweight noise-floor analysis and FIR design. Only the first few seconds
/// of the file are used, with a 1024-point FFT and a 257-tap linear-phase
/// filter, so loading and processing stay cheap.
/// </summary>
internal static class NoiseProfileAnalyzer
{
    internal const int FirTaps = 257;
    internal const int FirLatency = (FirTaps - 1) / 2;

    private const int FftSize = 1024;
    private const int FftLog2 = 10;
    private const int BinCount = FftSize / 2 + 1;
    private const int Hop = FftSize / 2;
    private const int TargetSampleRate = 48000;
    private const int MaxSeconds = 5;

    private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, float[] Profile)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static float[] Analyze(string path)
    {
        var file = new FileInfo(path);
        if (Cache.TryGetValue(path, out var cached) && cached.LastWriteUtc == file.LastWriteTimeUtc)
        {
            return cached.Profile;
        }

        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (source.WaveFormat.SampleRate != TargetSampleRate)
        {
            source = new WdlResamplingSampleProvider(source, TargetSampleRate);
        }

        int channels = Math.Max(1, source.WaveFormat.Channels);
        var window = BuildWindow(FftSize);
        var complex = new Complex[FftSize];
        var accumulator = new double[BinCount];
        var samples = new float[FftSize * channels];
        int framesProcessed = 0;
        int totalFrames = 0;
        int maxFrames = TargetSampleRate * MaxSeconds;
        int valid = ReadFrames(source, samples, 0, FftSize);

        while (valid > 0)
        {
            int windowFrames = Math.Min(valid, FftSize);
            for (int i = 0; i < FftSize; i++)
            {
                float mono = 0f;
                if (i < windowFrames)
                {
                    int offset = i * channels;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        mono += samples[offset + channel];
                    }

                    mono /= channels;
                }

                complex[i] = new Complex { X = mono * window[i], Y = 0f };
            }

            FastFourierTransform.FFT(true, FftLog2, complex);
            for (int bin = 0; bin < BinCount; bin++)
            {
                float real = complex[bin].X;
                float imaginary = complex[bin].Y;
                accumulator[bin] += Math.Sqrt(real * real + imaginary * imaginary);
            }

            framesProcessed++;
            totalFrames += windowFrames;
            if (windowFrames < FftSize || totalFrames >= maxFrames)
            {
                break;
            }

            Array.Copy(samples, Hop * channels, samples, 0, (FftSize - Hop) * channels);
            int read = ReadFrames(source, samples, (FftSize - Hop) * channels, Hop);
            valid = (FftSize - Hop) + read;
        }

        if (framesProcessed == 0)
        {
            throw new InvalidDataException("The file contains no audio.");
        }

        var raw = new float[BinCount];
        for (int i = 0; i < BinCount; i++)
        {
            raw[i] = (float)(accumulator[i] / framesProcessed);
        }

        var profile = new float[BinCount];
        float max = 0f;
        for (int i = 0; i < BinCount; i++)
        {
            float sum = 0f;
            int count = 0;
            for (int k = -2; k <= 2; k++)
            {
                int index = i + k;
                if (index < 0 || index >= BinCount)
                {
                    continue;
                }

                sum += raw[index];
                count++;
            }

            profile[i] = sum / Math.Max(1, count);
            max = Math.Max(max, profile[i]);
        }

        if (max <= 1e-9f)
        {
            throw new InvalidDataException("The noise-floor file is silent.");
        }

        for (int i = 0; i < BinCount; i++)
        {
            profile[i] /= max;
        }

        Cache[path] = (file.LastWriteTimeUtc, profile);
        return profile;
    }

    internal static float[] BuildKernel(float[] profile, float remove)
    {
        var response = new Complex[FftSize];
        for (int i = 0; i <= FftSize / 2; i++)
        {
            float gain = Math.Clamp(1f - remove * profile[i], 0f, 1f);
            response[i] = new Complex { X = gain, Y = 0f };
            if (i > 0 && i < FftSize / 2)
            {
                response[FftSize - i] = new Complex { X = gain, Y = 0f };
            }
        }

        FastFourierTransform.FFT(false, FftLog2, response);

        // The profile is already smoothed, so a rectangular truncation is
        // enough and keeps remove=0 as an exact delayed pass-through.
        var kernel = new float[FirTaps];
        int half = FirTaps / 2;
        for (int i = 0; i < FirTaps; i++)
        {
            // The inverse FFT is zero-phase (impulse at index 0). Shift it so
            // the causal kernel is centred at its midpoint.
            int index = (i - half + FftSize) % FftSize;
            kernel[i] = response[index].X / FftSize;
        }

        return kernel;
    }

    private static int ReadFrames(ISampleProvider source, float[] buffer, int offset, int frames)
    {
        int channels = Math.Max(1, source.WaveFormat.Channels);
        int wanted = frames * channels;
        int total = 0;
        while (total < wanted)
        {
            int read = source.Read(buffer.AsSpan(offset + total, wanted - total));
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total / channels;
    }

    private static float[] BuildWindow(int size)
    {
        var window = new float[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / Math.Max(1, size - 1)));
        }

        return window;
    }
}
