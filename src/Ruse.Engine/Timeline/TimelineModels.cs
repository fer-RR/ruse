using System.Text.Json.Serialization;

namespace Ruse.App.Audio;

public sealed class TimelineProject
{
    public int Version { get; set; } = 1;
    public int SampleRate { get; set; } = AudioEngineConfig.SampleRate;
    public double Tempo { get; set; } = 120.0;
    public long LoopStartSample { get; set; } = -1;
    public long LoopEndSample { get; set; } = -1;
    public List<TimelineTrack> Tracks { get; set; } = new();
}

public sealed class TimelineTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Track";
    public string? SourceNodeId { get; set; }
    public float Gain { get; set; } = 1f;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public List<TimelineClip> Clips { get; set; } = new();
    public List<TimelineAutomationLane> Automation { get; set; } = new();
}

public enum TimelineAutomationCurve
{
    Linear,
    Step,
    Exponential,
}

public sealed class TimelineAutomationPoint
{
    public long Sample { get; set; }
    public float Value { get; set; }
    public TimelineAutomationCurve Curve { get; set; } = TimelineAutomationCurve.Linear;
}

public sealed class TimelineAutomationLane
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetNodeId { get; set; } = "";
    public int ProcessorIndex { get; set; }
    public int ParameterIndex { get; set; }
    public List<TimelineAutomationPoint> Points { get; set; } = new();
}

public sealed class TimelineClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Audio";
    public string SourcePath { get; set; } = "";
    public long StartSample { get; set; }
    public long LengthSamples { get; set; }
    public long SourceOffsetSamples { get; set; }
    public float Gain { get; set; } = 1f;
    public int FadeInSamples { get; set; }
    public int FadeOutSamples { get; set; }

    public float[] Peaks { get; set; } = Array.Empty<float>();

    [JsonIgnore]
    public float[]? Audio { get; set; }
}
