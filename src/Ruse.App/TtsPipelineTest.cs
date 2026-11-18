using System.IO;
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await provider.StreamAsync(text, speed, voice, (samples, count) =>
        {
            int byteCount = count * 2 * sizeof(float);
            if (bytes.Length < byteCount) bytes = new byte[byteCount * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, byteCount);
            writer.Write(bytes, 0, byteCount);
            frames += count;
            callbacks++;
        }, timeout.Token).ConfigureAwait(false);
        writer.Flush();

        if (frames <= 0 || callbacks == 0)
        {
            throw new InvalidOperationException("The text-to-speech provider returned no audio.");
        }

        double elapsed = (DateTime.UtcNow - started).TotalSeconds;
        return $"model={modelId}; callbacks={callbacks}; frames={frames}; " +
               $"seconds={frames / 48000.0:0.00}; elapsed={elapsed:0.00}s; file={outputPath}";
    }

    public static async Task<string> RunBatchAsync(string modelId, string text)
    {
        var clauses = SpeechText.Clauses(text);
        if (clauses.Count == 0)
        {
            clauses.Add(text.Trim());
        }

        int count = 0;
        long bytes = 0;
        var paths = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await foreach (string path in TtsSynthesis.SynthesizeClausesAsync(
            modelId, clauses, 1f, 0, timeout.Token).ConfigureAwait(false))
        {
            count++;
            bytes += new FileInfo(path).Length;
            paths.Add(path);
        }

        if (count == 0 || bytes <= 0)
        {
            throw new InvalidOperationException("The text-to-speech host returned no audio.");
        }

        foreach (string path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }

        return $"model={modelId}; clauses={count}; bytes={bytes}; host=one";
    }
}
