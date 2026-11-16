using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Ruse.App.Diagnostics;
using Ruse.App.Audio;

namespace Ruse.App;

internal static class TranscriptionPipelineTest
{
    public static string Run(string modelId, string wavPath, int timeoutSeconds = 180)
    {
        int tapId = TranscriptionProcessor.NextTapId();
        TranscriptionProcessor.EnsureTap(tapId);
        try
        {
            using var session = new TranscriptionSession(tapId, modelId);
            Feed(tapId, wavPath);

            DateTime started = DateTime.UtcNow;
            DateTime deadline = started.AddSeconds(timeoutSeconds);
            TranscriptionSnapshot snapshot;
            while (true)
            {
                snapshot = session.Snapshot(includeCues: true);
                if (snapshot.Transcript.Length > 0 || snapshot.Error != null ||
                    DateTime.UtcNow >= deadline)
                {
                    break;
                }

                Thread.Sleep(250);
            }

            AppLog.Info(
                $"Transcription test: status={snapshot.Status}; text={snapshot.Transcript.Length}; " +
                $"level={snapshot.Level:0.###}; gpu={snapshot.GpuActive}; error={snapshot.Error}");
            return $"status={snapshot.Status}; text={snapshot.Transcript}; " +
                   $"cues={snapshot.Segments.Count}; level={snapshot.Level:0.###}; " +
                   $"gpu={snapshot.GpuActive}; detected={snapshot.DetectedLanguage}; " +
                   $"error={snapshot.Error}; elapsed={(DateTime.UtcNow - started).TotalSeconds:0.0}s";
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

        for (int i = 0; i < 600; i++)
        {
            TranscriptionProcessor.WriteTap(tapId, new float[480], 240);
            Thread.Sleep(5);
        }
    }
}
