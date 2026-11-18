using NAudio.Wave;

namespace Ruse.App;

internal static class TtsPipelineTest
{
    public static async Task<string> RunAsync(string modelId, string text, string outputPath, int voice = 0, float speed = 1f)
    {
        await using var provider = StreamingTtsProviders.Create(modelId);
        using var writer = new WaveFileWriter(outputPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        byte[] bytes = new byte[8192];
        long frames = 0;
        int callbacks = 0;
        var started = DateTime.UtcNow;
        await provider.StreamAsync(text, speed, voice, (samples, count) =>
        {
            int byteCount = count * 2 * sizeof(float);
            if (bytes.Length < byteCount) bytes = new byte[byteCount * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, byteCount);
            writer.Write(bytes, 0, byteCount);
            frames += count;
            callbacks++;
        }, CancellationToken.None).ConfigureAwait(false);
        writer.Flush();

        double elapsed = (DateTime.UtcNow - started).TotalSeconds;
        return $"model={modelId}; callbacks={callbacks}; frames={frames}; " +
               $"seconds={frames / 48000.0:0.00}; elapsed={elapsed:0.00}s; file={outputPath}";
    }
}
