using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>Reads a short, bounded waveform preview without loading the whole file.</summary>
public static class AudioFilePeaks
{
    public static float[] Read(string path, int count, double seconds, CancellationToken cancellationToken)
    {
        count = Math.Max(1, count);
        int targetFrames = Math.Max(1, (int)(Math.Max(0.1, seconds) * FormatConverter.TargetSampleRate));
        using var reader = new AudioFileReader(path);
        int channels = Math.Max(1, reader.WaveFormat.Channels);
        int sampleRate = Math.Max(1, reader.WaveFormat.SampleRate);
        var converter = new FormatConverter(channels, sampleRate, FormatConverter.TargetSampleRate);
        var source = new float[32768];
        var converted = new List<float>(65536);
        var peaks = new float[count];
        long frames = 0;

        while (frames < targetFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = ((ISampleProvider)reader).Read(source.AsSpan());
            if (read <= 0) break;
            converted.Clear();
            converter.Convert(source.AsSpan(0, read), converted);
            var span = CollectionsMarshal.AsSpan(converted);
            int blockFrames = span.Length / FormatConverter.TargetChannels;
            for (int frame = 0; frame < blockFrames && frames < targetFrames; frame++, frames++)
            {
                int bucket = (int)Math.Min(count - 1, frames * count / targetFrames);
                float magnitude = Math.Max(Math.Abs(span[frame * 2]), Math.Abs(span[(frame * 2) + 1]));
                if (magnitude > peaks[bucket]) peaks[bucket] = magnitude;
            }
        }

        return peaks;
    }
}
