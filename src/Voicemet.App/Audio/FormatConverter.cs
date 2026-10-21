namespace Voicemet.App.Audio;

/// <summary>
/// Converts arbitrary interleaved float PCM (any channel count, any sample rate)
/// into the mixer's canonical format: 48 kHz stereo interleaved float.
///
/// Channel mapping:
///   mono    → duplicated to L/R
///   stereo  → passthrough
///   >2 ch   → first two channels (L/R) kept, extras dropped
///
/// Sample-rate conversion uses linear interpolation, which is adequate for
/// routing/monitoring at these ratios and adds no allocation per callback.
/// </summary>
public sealed class FormatConverter
{
    public const int TargetSampleRate = 48000;
    public const int TargetChannels = 2;

    private readonly int _srcChannels;
    private readonly double _ratio;      // srcRate / targetRate
    private readonly bool _needsResample;
    private readonly bool _needsRemap;

    // Fractional position into the source stream, in source frames.
    private double _srcPos;

    // Carries the last source frame of the previous block so interpolation can
    // span block boundaries without a glitch.
    private float[] _prevFrame = new float[0];
    private bool _hasPrev;

    public FormatConverter(int sourceChannels, int sourceSampleRate)
    {
        _srcChannels = Math.Max(1, sourceChannels);
        _ratio = (double)sourceSampleRate / TargetSampleRate;
        _needsResample = sourceSampleRate != TargetSampleRate;
        _needsRemap = _srcChannels != TargetChannels;
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
            // Same rate: just remap channels straight through.
            for (int f = 0; f < srcFrames; f++)
            {
                MapFrame(source.Slice(f * _srcChannels, _srcChannels), destination);
            }

            return (destination.Count - startCount) / TargetChannels;
        }

        // Resample with linear interpolation over [prevFrame .. current block].
        // Virtual source index -1 refers to _prevFrame.
        int totalVirtualFrames = srcFrames + (_hasPrev ? 1 : 0);

        while (_srcPos < totalVirtualFrames - 1)
        {
            int i0 = (int)Math.Floor(_srcPos);
            double frac = _srcPos - i0;
            int i1 = i0 + 1;

            GetVirtualFrame(source, srcFrames, i0, out var a);
            GetVirtualFrame(source, srcFrames, i1, out var b);

            for (int c = 0; c < TargetChannels; c++)
            {
                int sc = MapChannelIndex(c);
                float sample = (float)(a[sc] * (1 - frac) + b[sc] * frac);
                destination.Add(sample);
            }

            _srcPos += _ratio;
        }

        // Consume integer frames that are fully behind the read cursor.
        int consumed = (int)Math.Floor(_srcPos);
        if (consumed > 0)
        {
            int keepFrameIndex = consumed - (_hasPrev ? 1 : 0);
            SaveLastFrame(source, srcFrames, keepFrameIndex);
            _srcPos -= consumed;
        }
        else
        {
            SaveLastFrame(source, srcFrames, srcFrames - 1);
        }

        return (destination.Count - startCount) / TargetChannels;
    }

    private int MapChannelIndex(int outChannel)
    {
        if (_srcChannels == 1)
        {
            return 0; // mono duplicated
        }

        return outChannel; // stereo passthrough / >2 keeps first two
    }

    private void MapFrame(ReadOnlySpan<float> frame, List<float> destination)
    {
        if (_srcChannels == 1)
        {
            destination.Add(frame[0]);
            destination.Add(frame[0]);
        }
        else
        {
            destination.Add(frame[0]);
            destination.Add(frame[1]);
        }
    }

    private void GetVirtualFrame(ReadOnlySpan<float> source, int srcFrames, int virtualIndex, out ReadOnlySpan<float> frame)
    {
        if (_hasPrev && virtualIndex == 0)
        {
            frame = _prevFrame;
            return;
        }

        int real = _hasPrev ? virtualIndex - 1 : virtualIndex;
        real = Math.Clamp(real, 0, srcFrames - 1);
        frame = source.Slice(real * _srcChannels, _srcChannels);
    }

    private void SaveLastFrame(ReadOnlySpan<float> source, int srcFrames, int frameIndex)
    {
        frameIndex = Math.Clamp(frameIndex, 0, srcFrames - 1);
        if (_prevFrame.Length != _srcChannels)
        {
            _prevFrame = new float[_srcChannels];
        }

        source.Slice(frameIndex * _srcChannels, _srcChannels).CopyTo(_prevFrame);
        _hasPrev = true;
    }
}
