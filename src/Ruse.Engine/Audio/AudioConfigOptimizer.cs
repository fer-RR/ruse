using NAudio.CoreAudioApi;

namespace Ruse.App.Audio;

/// <summary>
/// Finds a low-latency audio profile that is stable on this machine.
/// The test uses the default output device and a quiet tone; it never selects
/// an experimental ASIO endpoint.
/// </summary>
public static class AudioConfigOptimizer
{
    public sealed record Candidate(
        string Label,
        double BlockMs,
        string OutputBufferMode,
        int OutputBufferManualMs,
        double EngineCushionMs,
        bool ElasticRing);

    public sealed record Trial(
        Candidate Candidate,
        double MaxLatencyMs,
        double LastLatencyMs,
        long Underruns,
        long ShortReadFrames,
        long SkippedFrames,
        bool Running)
    {
        public bool Clean => Running &&
                             Underruns == 0 &&
                             ShortReadFrames == 0 &&
                             SkippedFrames == 0;
    }

    public sealed record Result(Trial Winner, IReadOnlyList<Trial> Trials);

    public static IReadOnlyList<Candidate> Candidates { get; } = new[]
    {
        new Candidate("5 ms / low", 5.0, "low", 0, 5.0, true),
        new Candidate("5 ms / manual 3 ms", 5.0, "manual", 3, 5.0, false),
        new Candidate("5 ms / balanced", 5.0, "balanced", 0, 5.0, true),
        new Candidate("5 ms / manual 4 ms", 5.0, "manual", 4, 5.0, false),
        new Candidate("7.5 ms / balanced", 7.5, "balanced", 0, 7.5, true),
        new Candidate("10 ms / safe", 10.0, "safe", 0, 10.0, false),
    };

    public static Result Run(Action<double, string>? progress = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        string deviceId;
        using (var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            deviceId = defaultDevice.ID;
        }

        var trials = new List<Trial>(Candidates.Count);
        for (int i = 0; i < Candidates.Count; i++)
        {
            Candidate candidate = Candidates[i];
            progress?.Invoke((double)i / Candidates.Count,
                $"Testing {candidate.Label} ({i + 1} of {Candidates.Count})\u2026");

            Trial trial = RunCandidate(enumerator, deviceId, candidate);
            trials.Add(trial);
            progress?.Invoke((double)(i + 1) / Candidates.Count,
                trial.Clean
                    ? $"{candidate.Label}: clean, {trial.MaxLatencyMs:0.0} ms."
                    : $"{candidate.Label}: {trial.Underruns} underrun(s), {trial.ShortReadFrames} short-read frame(s).");
        }

        Trial winner = trials
            .Where(trial => trial.Clean)
            .OrderBy(trial => trial.MaxLatencyMs)
            .ThenBy(trial => trial.Candidate.BlockMs)
            .FirstOrDefault()
            ?? trials
                .OrderBy(trial => trial.Underruns + trial.ShortReadFrames + trial.SkippedFrames)
                .ThenBy(trial => trial.MaxLatencyMs)
                .First();

        progress?.Invoke(1.0, $"Selected {winner.Candidate.Label}.");
        return new Result(winner, trials);
    }

    private static Trial RunCandidate(MMDeviceEnumerator enumerator, string deviceId, Candidate candidate)
    {
        Configure(candidate);

        MMDevice device = enumerator.GetDevice(deviceId);
        using var graph = new AudioGraph { AutoRun = false };
        var source = new SourceNode("Audio optimization", SourceKind.Tone);
        source.SetTone(1000, ToneWaveform.Sine, -46);
        var output = graph.AddOutput(device);
        graph.AddSource(source);
        graph.Connect(source, output);

        try
        {
            output.Start();
            source.Start();
            graph.StartEngine();

            // Let capture, the engine and the device settle before measuring.
            Thread.Sleep(400);
            long baselineUnderruns = output.Underruns;
            long baselineShortReads = output.ShortReadFrames;
            long baselineSkipped = source.SkippedFrames;
            double maxLatency = 0;
            double lastLatency = 0;
            for (int sample = 0; sample < 10; sample++)
            {
                Thread.Sleep(100);
                lastLatency = output.EstimatedLatencyMs;
                if (lastLatency > maxLatency)
                {
                    maxLatency = lastLatency;
                }
            }

            return new Trial(
                candidate,
                maxLatency,
                lastLatency,
                output.Underruns - baselineUnderruns,
                output.ShortReadFrames - baselineShortReads,
                source.SkippedFrames - baselineSkipped,
                output.IsRunning);
        }
        finally
        {
            source.Stop();
            output.Stop();
            graph.StopEngine();
        }
    }

    private static void Configure(Candidate candidate)
    {
        int blockFrames = AudioEngineConfig.FramesForMs(candidate.BlockMs);
        int cushionFrames = AudioEngineConfig.CushionFramesForMs(candidate.EngineCushionMs);
        int periods = candidate.OutputBufferMode.ToLowerInvariant() switch
        {
            "low" => 1,
            "safe" => 3,
            _ => 2,
        };

        AudioEngineConfig.Configure(blockFrames, cushionFrames, periods, candidate.OutputBufferManualMs);
        AudioEngineConfig.ElasticRing = candidate.ElasticRing;
        OutputNode.AggressiveCushion = candidate.OutputBufferMode == "low";
    }
}

