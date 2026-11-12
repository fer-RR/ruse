namespace Ruse.App;

internal sealed class AudioOptimizerStateDto
{
    public string Status { get; set; } = "idle";
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    public string? Error { get; set; }
    public AudioOptimizerResultDto? Result { get; set; }
}

internal sealed class AudioOptimizerResultDto
{
    public string Label { get; set; } = "";
    public double BlockMs { get; set; }
    public string OutputBufferMode { get; set; } = "balanced";
    public int OutputBufferManualMs { get; set; }
    public double EngineCushionMs { get; set; }
    public bool ElasticRing { get; set; }
    public double MaxLatencyMs { get; set; }
    public long Underruns { get; set; }
    public long ShortReadFrames { get; set; }
    public long SkippedFrames { get; set; }
    public bool Clean { get; set; }
    public List<AudioOptimizerTrialDto> Trials { get; set; } = new();
}

internal sealed class AudioOptimizerTrialDto
{
    public string Label { get; set; } = "";
    public double BlockMs { get; set; }
    public string OutputBufferMode { get; set; } = "balanced";
    public int OutputBufferManualMs { get; set; }
    public double EngineCushionMs { get; set; }
    public double MaxLatencyMs { get; set; }
    public long Underruns { get; set; }
    public long ShortReadFrames { get; set; }
    public long SkippedFrames { get; set; }
    public bool Clean { get; set; }
}
