using System.Runtime.InteropServices;

namespace Ruse.App.Audio;

/// <summary>
/// Converts arbitrary interleaved float PCM (any channel count, any sample rate)
/// into the mixer's canonical format: 48 kHz stereo interleaved float.
///
/// Channel mapping:
///   mono    -> duplicated to L/R
///   stereo  -> passthrough
///   3-6 ch  -> ITU-style L/R downmix (center and surrounds included)
///   >6 ch   -> first six channels use the 5.1 downmix; extra channels are ignored
///
/// Sample-rate conversion uses a precomputed windowed-sinc polyphase kernel.
/// The kernel is band-limited for downsampling, and all work is allocation-free
/// after construction so it can run on capture/ASIO threads.
/// </summary>
public sealed class FormatConverter
{
    public const int TargetSampleRate = 48000;
    public const int TargetChannels = 2;

    private const int Taps = 63;
    private const int Phases = 512;
    private const int MaxInputFrames = 8192;
    private const int Center = Taps / 2;
    private const float Surround = 0.70710678f;
    private const float Lfe = 0.5f;

    private readonly int _srcChannels;
    private readonly int _sourceRate;
    private readonly double _nominalRatio;
    private readonly double _resampleCutoff;
    private double _ratio;      // srcRate / targetRate, corrected for clock drift
    private bool _needsResample;
    private float[] _coefficients = Array.Empty<float>();

    // Streaming resampler history. Only used when rates differ.
    private float[] _history = Array.Empty<float>();
    private int _historyFrames;
    private double _position;

    /// <summary>Rate the converter produces.</summary>
    public int TargetRate { get; }

    public FormatConverter(int sourceChannels, int sourceSampleRate, int targetSampleRate = TargetSampleRate)
    {
        _srcChannels = Math.Max(1, sourceChannels);
        _sourceRate = Math.Max(1, sourceSampleRate);
        TargetRate = targetSampleRate > 0 ? targetSampleRate : TargetSampleRate;
        _nominalRatio = (double)_sourceRate / TargetRate;
        _ratio = _nominalRatio;
        _needsResample = _sourceRate != TargetRate;
        _resampleCutoff = Math.Min(1.0, (double)TargetRate / _sourceRate) * 0.97;

        if (_needsResample)
        {
            InitializeResampler();
        }
    }

    /// <summary>
    /// Corrects the conversion ratio by a measured clock error in parts per
    /// million. Values below 5 ppm are ignored; the correction is capped so a
    /// noisy estimator cannot audibly bend pitch.
    /// </summary>
    public void SetDriftPpm(double ppm)
    {
        if (!double.IsFinite(ppm))
        {
            return;
        }

        ppm = Math.Clamp(ppm, -1000.0, 1000.0);
        if (Math.Abs(ppm) < 5.0 && !_needsResample)
        {
            _ratio = _nominalRatio;
            return;
        }

        if (!_needsResample)
        {
            _needsResample = true;
            InitializeResampler();
        }

        _ratio = _nominalRatio * (1.0 + (ppm / 1_000_000.0));
    }

    private void InitializeResampler()
    {
        if (_coefficients.Length != 0)
        {
            return;
        }

        _coefficients = BuildCoefficients(_resampleCutoff);
        _history = new float[16384 * TargetChannels];
        _historyFrames = Center;
        _position = Center;
    }

    /// <summary>
    /// Converts a block of interleaved source frames and appends the 48 kHz
    /// stereo result to <paramref name="destination"/>. Returns frames written.
    /// </summary>
    public int Convert(ReadOnlySpan<float> source, List<float> destination)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        int srcFrames = source.Length / _srcChannels;
        if (srcFrames == 0)
        {
            return 0;
        }

        int startCount = destination.Count;

        if (!_needsResample)
        {
            int outputCount = startCount + (srcFrames * TargetChannels);
            destination.EnsureCapacity(outputCount);
            CollectionsMarshal.SetCount(destination, outputCount);
            var target = CollectionsMarshal.AsSpan(destination).Slice(startCount);
            if (_srcChannels == 2)
            {
                source[..(srcFrames * 2)].CopyTo(target);
            }
            else
            {
                for (int f = 0; f < srcFrames; f++)
                {
                    Downmix(source.Slice(f * _srcChannels, _srcChannels), out float left, out float right);
                    target[f * 2] = left;
                    target[f * 2 + 1] = right;
                }
            }

            return srcFrames;
        }

        destination.EnsureCapacity(startCount + (int)(srcFrames / _ratio * TargetChannels) + 8);
        int inputFrame = 0;
        while (inputFrame < srcFrames)
        {
            int chunkFrames = Math.Min(MaxInputFrames, srcFrames - inputFrame);
            AppendHistory(source.Slice(inputFrame * _srcChannels, chunkFrames * _srcChannels), chunkFrames);
            while (_position + Center < _historyFrames)
            {
                int baseFrame = (int)Math.Floor(_position) - Center;
                double fraction = _position - Math.Floor(_position);
                int phase = (int)(fraction * Phases + 0.5);
                if (phase >= Phases)
                {
                    phase = 0;
                    baseFrame++;
                }

                int coefficient = phase * Taps;
                float left = 0f, right = 0f;
                int sampleIndex = baseFrame * TargetChannels;
                for (int k = 0; k < Taps; k++)
                {
                    float h = _coefficients[coefficient + k];
                    left += _history[sampleIndex] * h;
                    right += _history[sampleIndex + 1] * h;
                    sampleIndex += TargetChannels;
                }

                destination.Add(left);
                destination.Add(right);
                _position += _ratio;
            }

            DropConsumedHistory();
            inputFrame += chunkFrames;
        }

        return (destination.Count - startCount) / TargetChannels;
    }

    private void AppendHistory(ReadOnlySpan<float> source, int srcFrames)
    {
        int needed = (_historyFrames + srcFrames) * TargetChannels;
        if (needed > _history.Length)
        {
            int newFrames = Math.Max(_historyFrames + srcFrames,
                Math.Max(16384, _history.Length / TargetChannels * 2));
            Array.Resize(ref _history, newFrames * TargetChannels);
        }

        int write = _historyFrames * TargetChannels;
        for (int f = 0; f < srcFrames; f++)
        {
            Downmix(source.Slice(f * _srcChannels, _srcChannels), out float left, out float right);
            _history[write++] = left;
            _history[write++] = right;
        }

        _historyFrames += srcFrames;
    }

    private void DropConsumedHistory()
    {
        int drop = (int)Math.Floor(_position) - Center;
        if (drop <= 0)
        {
            return;
        }

        drop = Math.Min(drop, _historyFrames);
        int remaining = _historyFrames - drop;
        if (remaining > 0)
        {
            Array.Copy(_history, drop * TargetChannels, _history, 0, remaining * TargetChannels);
        }

        _historyFrames = remaining;
        _position -= drop;
    }

    private void Downmix(ReadOnlySpan<float> frame, out float left, out float right)
    {
        if (_srcChannels <= 1)
        {
            left = right = frame[0];
            return;
        }

        left = frame[0];
        right = frame[1];

        if (_srcChannels >= 3)
        {
            float center = frame[2] * Surround;
            left += center;
            right += center;
        }

        if (_srcChannels >= 6)
        {
            float lfe = frame[3] * Lfe;
            left += lfe;
            right += lfe;
            left += frame[4] * Surround;
            right += frame[5] * Surround;
        }
        else if (_srcChannels == 5)
        {
            left += frame[3] * Surround;
            right += frame[4] * Surround;
        }
        else if (_srcChannels == 4)
        {
            left += frame[2] * Surround;
            right += frame[3] * Surround;
        }

        if (_srcChannels >= 8)
        {
            left += frame[6] * 0.5f;
            right += frame[7] * 0.5f;
        }
    }

    private static float[] BuildCoefficients(double cutoff)
    {
        var table = new float[Phases * Taps];
        var window = new double[Taps];
        for (int k = 0; k < Taps; k++)
        {
            double x = 2.0 * Math.PI * k / (Taps - 1);
            window[k] = 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2.0 * x);
        }

        for (int phase = 0; phase < Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            double sum = 0.0;
            int offset = phase * Taps;
            for (int k = 0; k < Taps; k++)
            {
                double x = (k - Center - fraction) * cutoff;
                double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double value = sinc * window[k];
                table[offset + k] = (float)value;
                sum += value;
            }

            if (Math.Abs(sum) < 1e-12)
            {
                continue;
            }

            float inverse = (float)(1.0 / sum);
            for (int k = 0; k < Taps; k++)
            {
                table[offset + k] *= inverse;
            }
        }

        return table;
    }
}
