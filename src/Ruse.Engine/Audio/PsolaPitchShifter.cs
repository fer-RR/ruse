namespace Ruse.App.Audio;

/// <summary>
/// Experimental time-domain PSOLA pitch shifter. Grains are copied at their
/// original rate and overlap-added at the target pitch period, which keeps the
/// input formants in place instead of resampling the whole voice.
/// </summary>
internal sealed class PsolaPitchShifter
{
    private const int SampleRate = 48000;
    private const int Capacity = 32768;
    private const int MaxHalf = 600;
    private const int Latency = 1536;
    private const int UnvoicedPeriod = 512;

    private readonly float[] _input = new float[Capacity];
    private readonly float[] _output = new float[Capacity];
    private long _inputWrite;
    private long _outputRead;
    private long _outputHorizon;
    private long _sourceMark;
    private long _outputMark;
    private bool _primed;

    public int LatencySamples => Latency;

    public void Process(Span<float> samples, int frames, float hz, float correctionSemitones, float mix)
    {
        if (frames <= 0 || samples.Length < frames * 2)
        {
            return;
        }

        WriteInput(samples, frames);
        float period = hz >= 70f && hz <= 1100f ? SampleRate / hz : UnvoicedPeriod;
        float targetPeriod = hz >= 70f && hz <= 1100f
            ? period / MathF.Pow(2f, correctionSemitones / 12f)
            : UnvoicedPeriod;
        period = Math.Clamp(period, 32f, MaxHalf);
        targetPeriod = Math.Clamp(targetPeriod, 32f, MaxHalf);

        if (!_primed)
        {
            _sourceMark = MaxHalf;
            _outputMark = Latency;
            _outputRead = 0;
            _outputHorizon = 0;
            _primed = true;
        }

        int half = Math.Max((int)MathF.Round(period), (int)MathF.Round(targetPeriod));
        half = Math.Clamp(half, 32, MaxHalf);
        float grainGain = targetPeriod / half;

        int generated = 0;
        while (_outputMark - half < _outputRead + frames + 512 && generated++ < 64)
        {
            long sourceStart = _sourceMark - half;
            long sourceEnd = _sourceMark + half;
            long outputStart = _outputMark - half;
            if (sourceStart < 0 || sourceEnd > _inputWrite)
            {
                break;
            }

            EnsureOutputHorizon(outputStart + (half * 2));
            for (int i = 0; i < half * 2; i++)
            {
                float window = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / Math.Max(1, half * 2 - 1));
                _output[Index(outputStart + i)] += _input[Index(sourceStart + i)] * window * grainGain;
            }

            _sourceMark += (int)MathF.Round(period);
            _outputMark += (int)MathF.Round(targetPeriod);

            long drift = _sourceMark - (_outputMark - Latency + half);
            if (drift > period / 2f)
            {
                _sourceMark -= (int)MathF.Round(period);
            }
            else if (drift < -period / 2f)
            {
                _sourceMark += (int)MathF.Round(period);
            }
        }

        EnsureOutputHorizon(_outputRead + frames);
        float wet = Math.Clamp(mix, 0f, 1f);
        float dry = 1f - wet;
        for (int i = 0; i < frames; i++)
        {
            long position = _outputRead + i;
            float wetSample = _output[Index(position)];
            long dryPosition = position - Latency;
            float drySample = dryPosition >= 0 ? _input[Index(dryPosition)] : 0f;
            samples[i * 2] = wetSample * wet + drySample * dry;
            samples[i * 2 + 1] = wetSample * wet + drySample * dry;
        }

        _outputRead += frames;
    }

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_output);
        _inputWrite = 0;
        _outputRead = 0;
        _outputHorizon = 0;
        _sourceMark = 0;
        _outputMark = 0;
        _primed = false;
    }

    private void WriteInput(ReadOnlySpan<float> samples, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            _input[Index(_inputWrite)] = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
            _inputWrite++;
        }
    }

    private void EnsureOutputHorizon(long end)
    {
        if (end <= _outputHorizon)
        {
            return;
        }

        long start = _outputHorizon;
        if (end - start > Capacity)
        {
            start = end - Capacity;
        }

        for (long position = start; position < end; position++)
        {
            _output[Index(position)] = 0f;
        }

        _outputHorizon = end;
    }

    private static int Index(long position) => (int)(position % Capacity);
}
