using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Ruse.App.Audio;

namespace Ruse.App;

/// <summary>Feeds a WAV through the translator tap without any audio hardware.</summary>
internal static class TranslationPipelineTest
{
    public static string Run(
        string speechModelId,
        string translationModelId,
        string targetCode,
        string wavPath,
        int timeoutSeconds = 180,
        string translationProvider = "qwen")
    {
        int tapId = TranscriptionProcessor.NextTapId();
        TranscriptionProcessor.EnsureTap(tapId);
        try
        {
            using var session = new TranslationSession(tapId, speechModelId, translationModelId, targetCode, translationProvider);
            Feed(tapId, wavPath);

            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            TranslationSnapshot snapshot;
            while (true)
            {
                snapshot = session.Snapshot(includeCues: true);
                if (snapshot.TranslatedText.Length > 0 || snapshot.Error != null ||
                    DateTime.UtcNow >= deadline)
                {
                    break;
                }

                Thread.Sleep(250);
            }

            return $"status={snapshot.Status}; cues={snapshot.TranslatedSegments.Count}; " +
                   $"source={snapshot.SourceText}; translated={snapshot.TranslatedText}; " +
                   $"detected={snapshot.DetectedLanguage}; error={snapshot.Error}";
        }
        finally
        {
            TranscriptionProcessor.RemoveTap(tapId);
        }
    }

    private static void Feed(int tapId, string wavPath)
    {
        using var reader = new AudioFileReader(wavPath);
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels == 1)
        {
            samples = new MonoToStereoSampleProvider(samples);
        }
        else if (samples.WaveFormat.Channels != 2)
        {
            throw new InvalidOperationException("Use a mono or stereo test WAV.");
        }

        samples = new WdlResamplingSampleProvider(samples, 48000);
        var buffer = new float[480];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;

        while (true)
        {
            int read = samples.Read(buffer.AsSpan());
            if (read <= 0) break;

            int frames = read / 2;
            long due = (long)(clock.Elapsed.TotalSeconds * 48000);
            while (produced >= due)
            {
                Thread.Sleep(1);
                due = (long)(clock.Elapsed.TotalSeconds * 48000);
            }

            TranscriptionProcessor.WriteTap(tapId, buffer.AsSpan(0, read), frames);
            produced += frames;
        }

        // Let the silence detector close the last chunk.
        for (int i = 0; i < 600; i++)
        {
            TranscriptionProcessor.WriteTap(tapId, new float[480], 240);
            Thread.Sleep(5);
        }
    }
}
