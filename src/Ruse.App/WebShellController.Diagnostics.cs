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
            OutputCount = _graph.Outputs.Count(output => !output.IsMonitor),
            OutputsRunning = _graph.Outputs.Count(output => !output.IsMonitor && output.IsRunning),
            TelemetryMs = _lastTelemetryMs,
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
        long shortReads = 0;
        bool lowLatency = false;
        foreach (var output in _graph.Outputs)
        {
            if (output.IsMonitor)
            {
                continue;
            }

            if (output.IsRunning)
            {
                worstLatency = Math.Max(worstLatency, output.EstimatedLatencyMs);
                chainLatency = Math.Max(chainLatency, output.ChainLatencyMs);
                lowLatency |= output.LowLatencyActive;
            }

            shortReads += output.ShortReadFrames;
        }

        diagnostics.WorstBacklogMs = worstBacklog;
        diagnostics.SkippedFrames = skippedFrames;
        diagnostics.WorstLatencyMs = worstLatency;
        diagnostics.ChainLatencyMs = chainLatency;
        diagnostics.ShortReadFrames = shortReads;
        diagnostics.LowLatency = lowLatency;

        SampleProcess(now, diagnostics);
        diagnostics.GpuPercent = GpuUsage.Sample();
        return diagnostics;
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
