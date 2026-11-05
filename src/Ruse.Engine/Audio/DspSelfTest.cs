using NAudio.Dsp;
using NAudio.Wave;

namespace Ruse.App.Audio;

public sealed record DspTestResult(bool Passed, string Summary);

/// <summary>
/// Deterministic, hardware-free checks for DSP primitives. Each check drives a
/// fixed signal through a processor and asserts level, spectrum or decay
/// behaviour; new effects should add a small number of these alongside their
/// catalog-wide smoke test in <see cref="LatencySelfTest.TestEffects"/>.
/// </summary>
public static class DspSelfTest
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BlockFrames = 240;
    private const int TestFrames = SampleRate;
    private const int FftSize = 8192;
    private const int FftLog2 = 13;
    private const int AnalysisStart = TestFrames / 2;
    private const int AnalysisFrames = TestFrames / 2;

    public static DspTestResult Run()
    {
        var failures = new List<string>();
        int checks = 0;

        void Check(string name, bool condition, string detail)
        {
            checks++;
            if (!condition)
            {
                failures.Add($"{name}: {detail}");
            }
        }

        var gainInput = Tone(1000.0, 0.25, TestFrames);
        var gain = new GainProcessor();
        Set(gain, "Gain", 0.5f);
        double gainRms = Rms(Process(gain, gainInput), AnalysisStart, AnalysisFrames);
        Check("gain", Math.Abs(gainRms - 0.0884) < 0.003,
            $"RMS {gainRms:0.###}, expected 0.088");

        var highPass = new HighPassProcessor();
        Set(highPass, "Cutoff", 1000f);
        double highPassPass = Rms(Process(highPass, Tone(2000.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        var highPassStopProcessor = new HighPassProcessor();
        Set(highPassStopProcessor, "Cutoff", 1000f);
        double highPassStop = Rms(Process(highPassStopProcessor, Tone(100.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        Check("high-pass", highPassPass > 0.15 && highPassStop < 0.02,
            $"2 kHz {highPassPass:0.###}, 100 Hz {highPassStop:0.###}");

        var lowPass = new BiquadProcessor(
            BiquadKind.LowPass, "dsp-lowpass", "DSP low-pass", 500f, "Frequency", hasQ: true);
        double lowPassPass = Rms(Process(lowPass, Tone(200.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        var lowPassStopProcessor = new BiquadProcessor(
            BiquadKind.LowPass, "dsp-lowpass", "DSP low-pass", 500f, "Frequency", hasQ: true);
        double lowPassStop = Rms(Process(lowPassStopProcessor, Tone(2000.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        Check("low-pass", lowPassPass > 0.15 && lowPassStop < 0.02,
            $"200 Hz {lowPassPass:0.###}, 2 kHz {lowPassStop:0.###}");

        var eq = new BiquadProcessor(
            BiquadKind.Peak, "dsp-eq", "DSP EQ", 1000f, "Frequency",
            hasGain: true, defaultGainDb: 12f, hasQ: true, defaultQ: 1f);
        double eqRms = Rms(Process(eq, Tone(1000.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        Check("eq boost", eqRms > 0.5, $"1 kHz RMS {eqRms:0.###}, expected > 0.5");

        var clipper = new ClipperProcessor();
        Set(clipper, "Drive", 12f);
        Set(clipper, "Ceiling", -6f);
        Set(clipper, "Mix", 1f);
        var clipped = Process(clipper, Tone(1000.0, 0.5, TestFrames));
        double clipPeak = Peak(clipped, AnalysisStart, AnalysisFrames);
        Check("clipper ceiling", clipPeak is > 0.4f and < 0.6f,
            $"peak {clipPeak:0.###}, expected about 0.5");

        var ring = new RingModulatorProcessor();
        Set(ring, "Frequency", 100f);
        Set(ring, "Mix", 1f);
        var ringOutput = Process(ring, Tone(1000.0, 0.25, TestFrames));
        double lower = AmplitudeAt(ringOutput, AnalysisStart, AnalysisFrames, 900.0);
        double upper = AmplitudeAt(ringOutput, AnalysisStart, AnalysisFrames, 1100.0);
        double carrier = AmplitudeAt(ringOutput, AnalysisStart, AnalysisFrames, 1000.0);
        Check("ring modulation", lower > 0.05 && upper > 0.05 && carrier < 0.02,
            $"900 Hz {lower:0.###}, 1100 Hz {upper:0.###}, 1 kHz {carrier:0.###}");

        var tail = new BiquadProcessor(
            BiquadKind.LowPass, "dsp-lowpass", "DSP low-pass", 500f, "Frequency", hasQ: true);
        var impulse = new float[TestFrames * Channels];
        impulse[0] = 1f;
        impulse[1] = 1f;
        var tailOutput = Process(tail, impulse);
        bool tailFinite = tailOutput.All(float.IsFinite);
        double tailRms = Rms(tailOutput, TestFrames - (SampleRate / 10), SampleRate / 10);
        Check("filter decay", tailFinite && tailRms < 1e-4,
            $"finite {tailFinite}, tail RMS {tailRms:0.###e+0}");

        double aliasDb = AliasEnergyDb(
            Tone(1000.0, 0.25, TestFrames), AnalysisStart, FftSize, 1000.0, harmonics: 1, halfWidthHz: 50.0);
        Check("alias measurement", aliasDb < -60.0,
            $"pure 1 kHz tone has {aliasDb:0.#} dB out-of-band energy");

        var moog = new MoogLadderProcessor();
        Set(moog, "Cutoff", 500f);
        Set(moog, "Resonance", 0.3f);
        double moogPass = Rms(Process(moog, Tone(200.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        var moogStopProcessor = new MoogLadderProcessor();
        Set(moogStopProcessor, "Cutoff", 500f);
        Set(moogStopProcessor, "Resonance", 0.3f);
        double moogStop = Rms(Process(moogStopProcessor, Tone(2000.0, 0.25, TestFrames)),
            AnalysisStart, AnalysisFrames);
        Check("moog ladder", moogPass > 0.05 && moogStop < 0.05,
            $"200 Hz {moogPass:0.###}, 2 kHz {moogStop:0.###}");

        var moogHot = new MoogLadderProcessor();
        Set(moogHot, "Cutoff", 1000f);
        Set(moogHot, "Resonance", 1f);
        Set(moogHot, "Drive", 12f);
        var moogHotOutput = Process(moogHot, Tone(1000.0, 0.5, TestFrames));
        bool moogHotFinite = moogHotOutput.All(float.IsFinite);
        double moogHotRms = Rms(moogHotOutput, AnalysisStart, AnalysisFrames);
        Check("moog resonance", moogHotFinite && moogHotRms < 8.0,
            $"finite {moogHotFinite}, RMS {moogHotRms:0.###}");

        var wah = new AutowahProcessor();
        Set(wah, "Sensitivity", 50f);
        Set(wah, "Range", 100f);
        Set(wah, "Mix", 100f);
        Set(wah, "Level", 100f);
        var wahOutput = Process(wah, Tone(1000.0, 0.25, TestFrames));
        bool wahFinite = wahOutput.All(float.IsFinite);
        double wahRms = Rms(wahOutput, AnalysisStart, AnalysisFrames);
        Check("autowah", wahFinite && wahRms > 0.02,
            $"finite {wahFinite}, RMS {wahRms:0.###}");

        var shifter = new FrequencyShifterProcessor();
        Set(shifter, "Shift", 100f);
        Set(shifter, "Mix", 100f);
        var upShift = Process(shifter, Tone(1000.0, 0.25, TestFrames));
        double up = AmplitudeAt(upShift, AnalysisStart, AnalysisFrames, 1100.0);
        double upDry = AmplitudeAt(upShift, AnalysisStart, AnalysisFrames, 1000.0);
        double upRms = Rms(upShift, AnalysisStart, AnalysisFrames);
        Check("frequency shifter up", up > 0.1 && upDry < 0.05,
            $"1.1 kHz {up:0.###}, 1 kHz {upDry:0.###}, RMS {upRms:0.###}");

        var downShifter = new FrequencyShifterProcessor();
        Set(downShifter, "Shift", -100f);
        Set(downShifter, "Mix", 100f);
        var downShift = Process(downShifter, Tone(1000.0, 0.25, TestFrames));
        double down = AmplitudeAt(downShift, AnalysisStart, AnalysisFrames, 900.0);
        double downDry = AmplitudeAt(downShift, AnalysisStart, AnalysisFrames, 1000.0);
        double downRms = Rms(downShift, AnalysisStart, AnalysisFrames);
        Check("frequency shifter down", down > 0.1 && downDry < 0.05,
            $"900 Hz {down:0.###}, 1 kHz {downDry:0.###}, RMS {downRms:0.###}");

        var bandlimited = new ToneOscillator(seed: 7);
        var bandlimitedSaw = new float[TestFrames * Channels];
        for (int frame = 0; frame < TestFrames; frame++)
        {
            float value = bandlimited.Next(ToneWaveform.Saw, 5000.0);
            bandlimitedSaw[frame * Channels] = value;
            bandlimitedSaw[(frame * Channels) + 1] = value;
        }

        var naiveSaw = NaiveSaw(5000.0, TestFrames);
        double bandlimitedAlias = AliasEnergyDb(
            bandlimitedSaw, AnalysisStart, FftSize, 5000.0, harmonics: 4, halfWidthHz: 50.0);
        double naiveAlias = AliasEnergyDb(
            naiveSaw, AnalysisStart, FftSize, 5000.0, harmonics: 4, halfWidthHz: 50.0);
        Check("bandlimited saw", bandlimitedAlias < naiveAlias - 6.0,
            $"bandlimited {bandlimitedAlias:0.#} dB vs naive {naiveAlias:0.#} dB");

        var pinkOscillator = new ToneOscillator(seed: 11);
        var pinkNoise = new float[TestFrames * Channels];
        var brownOscillator = new ToneOscillator(seed: 13);
        var brownNoise = new float[TestFrames * Channels];
        for (int frame = 0; frame < TestFrames; frame++)
        {
            float pink = pinkOscillator.Next(ToneWaveform.PinkNoise, 1000.0);
            float brown = brownOscillator.Next(ToneWaveform.BrownNoise, 1000.0);
            pinkNoise[frame * Channels] = pink;
            pinkNoise[(frame * Channels) + 1] = pink;
            brownNoise[frame * Channels] = brown;
            brownNoise[(frame * Channels) + 1] = brown;
        }

        double pinkRms = Rms(pinkNoise, 0, TestFrames);
        double brownRms = Rms(brownNoise, 0, TestFrames);
        Check("pink noise", pinkNoise.All(float.IsFinite) && pinkRms is > 0.01 and < 0.5,
            $"RMS {pinkRms:0.###}");
        Check("brown noise", brownNoise.All(float.IsFinite) && brownRms is > 0.01 and < 0.5,
            $"RMS {brownRms:0.###}");

        double sawRms = WaveRms(ToneWaveform.Saw, 1000.0);
        double squareRms = WaveRms(ToneWaveform.Square, 1000.0);
        double triangleRms = WaveRms(ToneWaveform.Triangle, 1000.0);
        Check("tone shapes", sawRms is > 0.2 and < 1.1 &&
                             squareRms is > 0.2 and < 1.1 &&
                             triangleRms is > 0.2 and < 1.1,
            $"saw {sawRms:0.###}, square {squareRms:0.###}, triangle {triangleRms:0.###}");

        var noiseCarrier = NoiseBuffer(TestFrames, 17, 0.3);
        var modulator = Tone(1000.0, 0.25, TestFrames);
        var vocoder = new VocoderProcessor();
        Set(vocoder, "Attack", 10f);
        Set(vocoder, "Release", 60f);
        Set(vocoder, "Bandwidth", 1f);
        Set(vocoder, "Mix", 100f);
        Set(vocoder, "Level", 100f);
        var vocoded = ProcessWithSidechain(vocoder, noiseCarrier, modulator);
        double vocodedLow = AmplitudeAt(vocoded, AnalysisStart, AnalysisFrames, 1000.0);
        double vocodedHigh = AmplitudeAt(vocoded, AnalysisStart, AnalysisFrames, 4000.0);
        Check("vocoder", vocodedLow > 0.002 && vocodedHigh < vocodedLow * 0.5,
            $"1 kHz {vocodedLow:0.####}, 4 kHz {vocodedHigh:0.####}");

        var dryVocoder = new VocoderProcessor();
        var dryCarrier = Process(dryVocoder, noiseCarrier);
        double dryCarrierRms = Rms(dryCarrier, AnalysisStart, AnalysisFrames);
        double sourceRms = Rms(noiseCarrier, AnalysisStart, AnalysisFrames);
        Check("vocoder dry fallback", Math.Abs(dryCarrierRms - sourceRms) < 0.01,
            $"dry {dryCarrierRms:0.###}, source {sourceRms:0.###}");

        var burst = new float[TestFrames * Channels];
        int burstFrames = TestFrames / 10;
        for (int frame = 0; frame < burstFrames; frame++)
        {
            float value = (float)(0.5 * Math.Sin(2.0 * Math.PI * 220.0 * frame / SampleRate));
            burst[frame * Channels] = value;
            burst[(frame * Channels) + 1] = value;
        }

        var resonator = new StringResonatorProcessor();
        Set(resonator, "Frequency", 220f);
        Set(resonator, "Decay", 80f);
        Set(resonator, "Brightness", 70f);
        Set(resonator, "Mix", 100f);
        Set(resonator, "Level", 100f);
        var resonatorOutput = Process(resonator, burst);
        bool ringFinite = resonatorOutput.All(float.IsFinite);
        double early = Rms(resonatorOutput, SampleRate / 20, SampleRate / 5);
        double late = Rms(resonatorOutput, SampleRate * 4 / 5, SampleRate / 5);
        Check("string resonator", ringFinite && early > 0.01 && late < early * 0.2,
            $"finite {ringFinite}, early {early:0.###}, late {late:0.####}");

        var reverseInput = Tone(220.0, 0.2, TestFrames);
        var reverseOutput = (float[])reverseInput.Clone();
        var reverse = new ReverseDelayProcessor();
        Set(reverse, "Time", 500f);
        Set(reverse, "Mix", 100f);
        Set(reverse, "Level", 100f);
        reverse.Reset();
        int transitionFrame = TestFrames * 3 / 4;
        for (int offset = 0; offset < TestFrames; offset += BlockFrames)
        {
            if (offset == transitionFrame)
            {
                Set(reverse, "Time", 150f);
            }

            int count = Math.Min(BlockFrames, TestFrames - offset);
            reverse.Process(
                reverseOutput.AsSpan(offset * Channels, count * Channels), count);
        }

        float maxJump = 0f;
        int maxJumpFrame = 0;
        int jumpStart = Math.Max(1, transitionFrame - 1) * Channels;
        int jumpEnd = Math.Min(TestFrames, transitionFrame + 4096) * Channels;
        for (int i = jumpStart + Channels; i < jumpEnd; i += Channels)
        {
            float jump = MathF.Abs(reverseOutput[i] - reverseOutput[i - Channels]);
            if (jump > maxJump)
            {
                maxJump = jump;
                maxJumpFrame = i / Channels;
            }
        }

        Check("reverse delay crossfade",
            reverseOutput.All(float.IsFinite) && maxJump < 0.02f,
            $"finite {reverseOutput.All(float.IsFinite)}, max jump {maxJump:0.####} at frame {maxJumpFrame}");

        var wavesetInput = Tone(220.0, 0.2, TestFrames);
        var wavesetOutput = (float[])wavesetInput.Clone();
        var waveset = new WavesetProcessor();
        Set(waveset, "Length", 200f);
        Set(waveset, "Repeats", 2f);
        Set(waveset, "Mix", 100f);
        Set(waveset, "Level", 100f);
        waveset.Reset();
        for (int offset = 0; offset < TestFrames; offset += BlockFrames)
        {
            int count = Math.Min(BlockFrames, TestFrames - offset);
            waveset.Process(
                wavesetOutput.AsSpan(offset * Channels, count * Channels), count);
        }

        float wavesetJump = 0f;
        int wavesetJumpStart = (SampleRate / 10) * Channels;
        for (int i = wavesetJumpStart + Channels; i < wavesetOutput.Length; i += Channels)
        {
            wavesetJump = Math.Max(
                wavesetJump, MathF.Abs(wavesetOutput[i] - wavesetOutput[i - Channels]));
        }

        Check("waveset transitions",
            wavesetOutput.All(float.IsFinite) && wavesetJump < 0.03f,
            $"finite {wavesetOutput.All(float.IsFinite)}, max jump {wavesetJump:0.####}");

        var pitchTone = Tone(220.0, 0.25, TestFrames);
        var autocorrelation = new NotePitchTracker(highResolution: true);
        var amdf = new AmdfPitchTracker(highResolution: true);
        for (int offset = 0; offset < TestFrames; offset += BlockFrames)
        {
            int count = Math.Min(BlockFrames, TestFrames - offset);
            var block = pitchTone.AsSpan(offset * Channels, count * Channels);
            autocorrelation.Add(block, count);
            amdf.Add(block, count);
        }

        bool autoPitch = autocorrelation.TryGetPitch(out float autoHz) &&
                         Math.Abs(autoHz - 220f) < 5f &&
                         autocorrelation.Confidence > 0.3f;
        bool amdfPitch = amdf.TryGetPitch(out float amdfHz) &&
                         Math.Abs(amdfHz - 220f) < 5f &&
                         amdf.Confidence > 0.3f;
        Check("pitch trackers", autoPitch && amdfPitch,
            $"autocorrelation {autoHz:0.#} Hz ({autocorrelation.Confidence:0.##}), " +
            $"AMDF {amdfHz:0.#} Hz ({amdf.Confidence:0.##})");

        string noisePath = Path.Combine(
            Path.GetTempPath(), $"ruse-noise-{Guid.NewGuid():N}.wav");
        try
        {
            var random = new Random(23);
            using (var writer = new WaveFileWriter(
                noisePath, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels)))
            {
                var noise = new float[SampleRate * Channels / 2];
                for (int i = 0; i < noise.Length; i++)
                {
                    noise[i] = (float)(((random.NextDouble() * 2.0) - 1.0) * 0.05);
                }

                writer.WriteSamples(noise, 0, noise.Length);
            }

            var suppressor = new NoiseSuppressionProcessor();
            suppressor.LoadNoiseProfile(noisePath);
            var suppressed = Process(suppressor, Tone(1000.0, 0.25, TestFrames));
            bool suppressorFinite = suppressed.All(float.IsFinite);
            double suppressorRms = Rms(suppressed, AnalysisStart, AnalysisFrames);
            Check("noise profile FIR", suppressorFinite && suppressorRms > 0.001,
                $"finite {suppressorFinite}, RMS {suppressorRms:0.###}");
        }
        finally
        {
            try { File.Delete(noisePath); } catch { }
        }

        string summary = failures.Count == 0
            ? $"{checks} checks OK (DSP primitives, new effects and bandlimited tone)."
            : $"{failures.Count} of {checks} checks failed: " + string.Join("; ", failures);
        return new DspTestResult(failures.Count == 0, summary);
    }

    private static void Set(IParameterizedProcessor processor, string name, float value)
    {
        var parameter = processor.Parameters.FirstOrDefault(candidate => candidate.Name == name);
        if (parameter == null)
        {
            throw new InvalidOperationException($"Missing parameter '{name}'.");
        }

        parameter.Set(value);
    }

    private static float[] Process(IAudioProcessor processor, float[] input)
    {
        int frames = input.Length / Channels;
        var output = (float[])input.Clone();
        processor.Reset();
        for (int offset = 0; offset < frames; offset += BlockFrames)
        {
            int count = Math.Min(BlockFrames, frames - offset);
            processor.Process(output.AsSpan(offset * Channels, count * Channels), count);
        }

        return output;
    }

    private static float[] Tone(double frequency, double amplitude, int frames)
    {
        var buffer = new float[frames * Channels];
        double increment = 2.0 * Math.PI * frequency / SampleRate;
        double phase = 0.0;
        for (int frame = 0; frame < frames; frame++)
        {
            float value = (float)(amplitude * Math.Sin(phase));
            buffer[frame * Channels] = value;
            buffer[(frame * Channels) + 1] = value;
            phase += increment;
            if (phase >= 2.0 * Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }
        }

        return buffer;
    }

    private static float[] ProcessWithSidechain(
        VocoderProcessor processor, float[] input, float[] key)
    {
        int frames = input.Length / Channels;
        var output = (float[])input.Clone();
        processor.Reset();
        for (int offset = 0; offset < frames; offset += BlockFrames)
        {
            int count = Math.Min(BlockFrames, frames - offset);
            processor.SetSidechain(
                key.AsSpan(offset * Channels, count * Channels), count);
            processor.Process(output.AsSpan(offset * Channels, count * Channels), count);
        }

        return output;
    }

    private static float[] NoiseBuffer(int frames, int seed, double amplitude)
    {
        var random = new Random(seed);
        var buffer = new float[frames * Channels];
        for (int frame = 0; frame < frames; frame++)
        {
            float value = (float)(((random.NextDouble() * 2.0) - 1.0) * amplitude);
            buffer[frame * Channels] = value;
            buffer[(frame * Channels) + 1] = value;
        }

        return buffer;
    }

    private static double WaveRms(ToneWaveform waveform, double frequency)
    {
        var oscillator = new ToneOscillator(seed: 5);
        double sum = 0.0;
        for (int frame = 0; frame < TestFrames; frame++)
        {
            double value = oscillator.Next(waveform, frequency);
            sum += value * value;
        }

        return Math.Sqrt(sum / TestFrames);
    }

    private static float[] NaiveSaw(double frequency, int frames)
    {
        var buffer = new float[frames * Channels];
        double increment = 2.0 * Math.PI * frequency / SampleRate;
        double phase = 0.0;
        for (int frame = 0; frame < frames; frame++)
        {
            float value = (float)((phase / Math.PI) - 1.0);
            buffer[frame * Channels] = value;
            buffer[(frame * Channels) + 1] = value;
            phase += increment;
            if (phase >= 2.0 * Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }
        }

        return buffer;
    }

    private static double Rms(ReadOnlySpan<float> samples, int startFrame, int frames)
    {
        double sum = 0.0;
        int start = startFrame * Channels;
        int count = frames * Channels;
        for (int i = 0; i < count; i++)
        {
            sum += (double)samples[start + i] * samples[start + i];
        }

        return count == 0 ? 0.0 : Math.Sqrt(sum / count);
    }

    private static float Peak(ReadOnlySpan<float> samples, int startFrame, int frames)
    {
        float peak = 0f;
        int start = startFrame * Channels;
        int count = frames * Channels;
        for (int i = 0; i < count; i++)
        {
            float magnitude = MathF.Abs(samples[start + i]);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    private static double AmplitudeAt(ReadOnlySpan<float> samples, int startFrame, int frames, double frequency)
    {
        double omega = 2.0 * Math.PI * frequency / SampleRate;
        double coefficient = 2.0 * Math.Cos(omega);
        double s1 = 0.0;
        double s2 = 0.0;
        int start = startFrame * Channels;
        for (int frame = 0; frame < frames; frame++)
        {
            double s0 = samples[start + (frame * Channels)] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        double real = s1 - (s2 * Math.Cos(omega));
        double imaginary = s2 * Math.Sin(omega);
        return Math.Sqrt((real * real) + (imaginary * imaginary)) * 2.0 / frames;
    }

    /// <summary>
    /// Sums FFT power outside the harmonics of a test tone. Used by the
    /// bandlimited-oscillator tests to measure aliasing and by this harness to
    /// sanity-check itself with a pure sine.
    /// </summary>
    private static double AliasEnergyDb(
        ReadOnlySpan<float> samples,
        int startFrame,
        int frames,
        double fundamental,
        int harmonics,
        double halfWidthHz)
    {
        int length = Math.Min(FftSize, Math.Max(0, frames));
        if (length < 2)
        {
            return 0.0;
        }

        var spectrum = new Complex[FftSize];
        int start = startFrame * Channels;
        for (int i = 0; i < length; i++)
        {
            double window = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (length - 1)));
            spectrum[i].X = (float)(samples[start + (i * Channels)] * window);
            spectrum[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftLog2, spectrum);

        double pass = 0.0;
        double stop = 0.0;
        double binHz = (double)SampleRate / FftSize;
        for (int bin = 1; bin < FftSize / 2; bin++)
        {
            double power = ((double)spectrum[bin].X * spectrum[bin].X) +
                           ((double)spectrum[bin].Y * spectrum[bin].Y);
            double hz = bin * binHz;
            bool harmonic = false;
            for (int h = 1; h <= harmonics; h++)
            {
                if (Math.Abs(hz - (fundamental * h)) <= halfWidthHz)
                {
                    harmonic = true;
                    break;
                }
            }

            if (harmonic)
            {
                pass += power;
            }
            else
            {
                stop += power;
            }
        }

        return 10.0 * Math.Log10((stop + 1e-30) / (pass + 1e-30));
    }
}
