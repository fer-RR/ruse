using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>Estimates the stable musical note of a monophonic sample.</summary>
public static class SampleNoteDetector
{
    private const int TargetRate = 48000;
    private const int MaxSeconds = 20;
    private const int BlockFrames = 512;

    public static int DetectRootMidi(string path, out float confidence)
    {
        confidence = 0;
        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider sampleProvider = reader.ToSampleProvider();
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            int sourceRate = Math.Max(8000, reader.WaveFormat.SampleRate);
            int maxFrames = sourceRate * MaxSeconds;
            var mono = new List<float>(Math.Min(maxFrames, sourceRate * 5));
            var buffer = new float[8192 * channels];
            while (mono.Count < maxFrames)
            {
                int wanted = Math.Min(buffer.Length, (maxFrames - mono.Count) * channels);
                int read = sampleProvider.Read(buffer.AsSpan(0, wanted));
                if (read <= 0)
                {
                    break;
                }

                int frames = read / channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    int index = frame * channels;
                    float sum = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        sum += buffer[index + channel];
                    }

                    mono.Add(sum / channels);
                }
            }

            if (mono.Count < sourceRate / 4)
            {
                return 0;
            }

            float[] monoSamples = Resample(mono, sourceRate);
            var tracker = new NotePitchTracker(highResolution: true);
            var stereo = new float[BlockFrames * 2];
            var pitches = new List<float>();
            float confidenceSum = 0;
            for (int offset = 0; offset + BlockFrames <= monoSamples.Length; offset += BlockFrames)
            {
                for (int frame = 0; frame < BlockFrames; frame++)
                {
                    stereo[frame * 2] = monoSamples[offset + frame];
                    stereo[frame * 2 + 1] = monoSamples[offset + frame];
                }

                tracker.Add(stereo, BlockFrames);
                if (tracker.TryGetPitch(out float hz) &&
                    tracker.Confidence >= 0.5f &&
                    hz >= 40f && hz <= 2000f)
                {
                    pitches.Add(hz);
                    confidenceSum += tracker.Confidence;
                }
            }

            if (pitches.Count == 0)
            {
                return 0;
            }

            pitches.Sort();
            float median = pitches[pitches.Count / 2];
            int midi = (int)Math.Round(69 + 12 * Math.Log2(median / 440.0));
            confidence = confidenceSum / pitches.Count;
            return Math.Clamp(midi, 0, 127);
        }
        catch
        {
            return 0;
        }
    }

    private static float[] Resample(List<float> samples, int sourceRate)
    {
        if (sourceRate == TargetRate)
        {
            return samples.ToArray();
        }

        int outputFrames = Math.Max(1, (int)Math.Round(samples.Count * (double)TargetRate / sourceRate));
        var output = new float[outputFrames];
        double step = (double)sourceRate / TargetRate;
        for (int frame = 0; frame < outputFrames; frame++)
        {
            double position = frame * step;
            int index = (int)position;
            float fraction = (float)(position - index);
            int a = Math.Min(index, samples.Count - 1);
            int b = Math.Min(index + 1, samples.Count - 1);
            output[frame] = samples[a] + (samples[b] - samples[a]) * fraction;
        }

        return output;
    }
}
