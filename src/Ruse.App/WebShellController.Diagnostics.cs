using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly System.Diagnostics.Process _process = System.Diagnostics.Process.GetCurrentProcess();
    private long _lastCpuTick;
    private TimeSpan _lastCpuTime;
    private double _lastCpuPercent;
    private double _lastCpuShare;
    private bool _cpuPrimed;
    private double _lastTelemetryMs;
    private readonly Dictionary<string, SourceMetricState> _sourceMetrics = new();
    private readonly double[] _latencyHistory = new double[120];
    private int _latencyHistoryCount;
    private int _latencyHistoryIndex;
    private long _rateLastTick;
    private long _rateLastSkipped;
    private long _rateLastUnderruns;
    private double _rateUnderrunsPerMinute;
    private double _rateSkippedPerMinute;

    private sealed class SourceMetricState
    {
        public long FirstTick { get; init; }
        public double FirstBufferedMs { get; init; }
        public long FirstFrames { get; init; }
    }

    /// <summary>Snapshots the engine, process and UI costs for the diagnostics overlay.</summary>
    private DiagnosticsDto BuildDiagnostics(long now)
    {
        var diagnostics = new DiagnosticsDto
        {
            RenderTier = System.Windows.Media.RenderCapability.Tier >> 16,
            NodeCount = _nodes.Count,
            LinkCount = _links.Count,
            SourceCount = _graph.Sources.Count,
            SourcesRunning = _graph.Sources.Count(source => source.IsRunning),
            OutputCount = _graph.Outputs.Count,
            OutputsRunning = _graph.Outputs.Count(output => output.IsRunning),
            TelemetryMs = _lastTelemetryMs,
            EngineBlockMs = AudioEngineConfig.BlockFrames * 1000.0 / AudioEngineConfig.SampleRate,
            AudioFormat = $"{AudioEngineConfig.SampleRate / 1000.0:0.#} kHz · stereo · float32",
        };

        double worstBacklog = 0;
        long skippedFrames = 0;
        foreach (var source in _graph.Sources)
        {
            worstBacklog = Math.Max(worstBacklog, source.BufferedMs);
            skippedFrames += source.SkippedFrames;
        }

        double worstLatency = 0;
        double chainLatency = 0;
        double outputTarget = 0;
        double outputBuffered = 0;
        double endpointLatency = 0;
        double engineQueue = 0;
        double outputRingFill = 0;
        long shortReads = 0;
        long outputUnderruns = 0;
        bool lowLatency = false;
        foreach (var output in _graph.Outputs)
        {

            if (output.IsRunning)
            {
                worstLatency = Math.Max(worstLatency, output.EstimatedLatencyMs);
                chainLatency = Math.Max(chainLatency, output.ChainLatencyMs);
                outputTarget = Math.Max(outputTarget, output.TargetCushionMs);
                outputBuffered = Math.Max(outputBuffered, output.BufferedMs);
                endpointLatency = Math.Max(endpointLatency, output.EndpointLatencyMs);
                engineQueue = Math.Max(engineQueue, output.EngineQueueMs);
                outputRingFill = Math.Max(outputRingFill, output.EngineQueuePercent);
                lowLatency |= output.LowLatencyActive;
            }

            shortReads += output.ShortReadFrames;
            outputUnderruns += output.Underruns;
        }

        diagnostics.WorstBacklogMs = worstBacklog;
        diagnostics.SkippedFrames = skippedFrames;
        diagnostics.WorstLatencyMs = worstLatency;
        diagnostics.ChainLatencyMs = chainLatency;
        diagnostics.OutputTargetMs = outputTarget;
        diagnostics.OutputBufferedMs = outputBuffered;
        diagnostics.EndpointLatencyMs = endpointLatency;
        diagnostics.EngineQueueMs = engineQueue;
        diagnostics.ShortReadFrames = shortReads;
        diagnostics.OutputUnderruns = outputUnderruns;
        diagnostics.LowLatency = lowLatency;
        UpdateMeasurementStats(now, skippedFrames, outputUnderruns, outputRingFill, worstLatency, diagnostics);
        diagnostics.GpuAvailable = ModelRuntime.GpuAvailable;
        diagnostics.GpuDeviceName = ModelRuntime.GpuDeviceName;
        diagnostics.GpuStatusError = ModelRuntime.GpuStatusError;
        diagnostics.GpuFallbackReason = ModelRuntime.GpuFallbackReason;

        SampleProcess(now, diagnostics);
        diagnostics.GpuPercent = GpuUsage.Sample();
        return diagnostics;
    }

    private void UpdateMeasurementStats(
        long now,
        long skippedFrames,
        long outputUnderruns,
        double outputRingFill,
        double worstLatency,
        DiagnosticsDto diagnostics)
    {
        double worstFill = 0;
        double worstDrift = 0;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in _graph.Sources)
        {
            if (source.Kind == SourceKind.File || !source.IsRunning)
            {
                continue;
            }

            active.Add(source.Id);
            if (!_sourceMetrics.TryGetValue(source.Id, out var state))
            {
                _sourceMetrics[source.Id] = new SourceMetricState
                {
                    FirstTick = now,
                    FirstBufferedMs = source.BufferedMs,
                    FirstFrames = source.SourceFrames,
                };
                continue;
            }

            double seconds = (now - state.FirstTick) / 1000.0;
            if (seconds < 1.0)
            {
                continue;
            }

            double fill = (source.BufferedMs - state.FirstBufferedMs) / seconds;
            if (Math.Abs(fill) > Math.Abs(worstFill))
            {
                worstFill = fill;
            }

            if (source.SampleRate > 0)
            {
                long frames = source.SourceFrames - state.FirstFrames;
                if (frames >= 0)
                {
                    double ppm = ((frames / seconds) / source.SampleRate - 1.0) * 1_000_000.0;
                    if (Math.Abs(ppm) > Math.Abs(worstDrift))
                    {
                        worstDrift = ppm;
                    }
                }
            }
        }

        foreach (string key in _sourceMetrics.Keys.ToArray())
        {
            if (!active.Contains(key))
            {
                _sourceMetrics.Remove(key);
            }
        }

        diagnostics.SourceFillRateMsPerSec = worstFill;
        diagnostics.EstimatedDriftPpm = worstDrift;
        diagnostics.OutputRingFillPercent = outputRingFill;

        if (_rateLastTick > 0)
        {
            double minutes = (now - _rateLastTick) / 60_000.0;
            if (minutes > 0.0)
            {
                long skippedDelta = skippedFrames - _rateLastSkipped;
                long underrunDelta = outputUnderruns - _rateLastUnderruns;
                if (skippedDelta < 0) skippedDelta = 0;
                if (underrunDelta < 0) underrunDelta = 0;
                double skippedRate = skippedDelta / minutes;
                double underrunRate = underrunDelta / minutes;
                _rateSkippedPerMinute = _rateSkippedPerMinute * 0.7 + skippedRate * 0.3;
                _rateUnderrunsPerMinute = _rateUnderrunsPerMinute * 0.7 + underrunRate * 0.3;
            }
        }

        _rateLastTick = now;
        _rateLastSkipped = skippedFrames;
        _rateLastUnderruns = outputUnderruns;
        diagnostics.SkippedFramesPerMinute = _rateSkippedPerMinute;
        diagnostics.UnderrunsPerMinute = _rateUnderrunsPerMinute;

        if (worstLatency > 0)
        {
            _latencyHistory[_latencyHistoryIndex] = worstLatency;
            _latencyHistoryIndex = (_latencyHistoryIndex + 1) % _latencyHistory.Length;
            _latencyHistoryCount = Math.Min(_latencyHistoryCount + 1, _latencyHistory.Length);
        }

        if (_latencyHistoryCount == 0)
        {
            return;
        }

        double sum = 0;
        for (int i = 0; i < _latencyHistoryCount; i++)
        {
            sum += _latencyHistory[i];
        }

        double mean = sum / _latencyHistoryCount;
        double variance = 0;
        for (int i = 0; i < _latencyHistoryCount; i++)
        {
            double delta = _latencyHistory[i] - mean;
            variance += delta * delta;
        }

        diagnostics.LatencyMeanMs = mean;
        diagnostics.LatencyStdDevMs = Math.Sqrt(variance / _latencyHistoryCount);
    }

    private void SampleProcess(long now, DiagnosticsDto diagnostics)
    {
        try
        {
            TimeSpan cpu = _process.TotalProcessorTime;
            if (!_cpuPrimed)
            {
                _cpuPrimed = true;
                _lastCpuTick = now;
                _lastCpuTime = cpu;
            }
            else if (now - _lastCpuTick >= 250)
            {
                double wallMs = now - _lastCpuTick;
                double usedMs = (cpu - _lastCpuTime).TotalMilliseconds;
                _lastCpuPercent = Math.Clamp(usedMs / wallMs * 100.0, 0, Environment.ProcessorCount * 100.0);
                _lastCpuShare = _lastCpuPercent / Environment.ProcessorCount;
                _lastCpuTick = now;
                _lastCpuTime = cpu;
            }

            diagnostics.CpuPercent = _lastCpuPercent;
            diagnostics.CpuShare = _lastCpuShare;
            diagnostics.WorkingSetMb = _process.WorkingSet64 / 1024.0 / 1024.0;
            diagnostics.Threads = _process.Threads.Count;
            diagnostics.ProcessUptimeSeconds = Math.Max(0, (DateTime.Now - _process.StartTime).TotalSeconds);
        }
        catch
        {
            // Process counters can fail during shutdown; the overlay can show dashes.
        }

        diagnostics.ManagedMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
    }
}
