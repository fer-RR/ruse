using System.Text.Json;

namespace Ruse.App;

/// <summary>JSON contracts for the WebView2 shell. No WPF or NAudio types cross this boundary.</summary>
internal sealed class CatalogItemDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Details { get; set; } = "";
    public uint? ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public bool Active { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Direction { get; set; }
    public string? Provider { get; set; }
    public bool Virtual { get; set; }
    public bool IsDefault { get; set; }
    public string? Badge { get; set; }
    public string? MicHint { get; set; }
    public string? IconDataUrl { get; set; }
    public List<ParameterDto> Parameters { get; set; } = new();
    public List<ChainElementDto> Elements { get; set; } = new();
}

internal sealed class ChainElementDto
{
    public string TypeId { get; set; } = "";
    public Dictionary<string, float> Parameters { get; set; } = new();
}

internal sealed class ChainSaveDto
{
    public string Name { get; set; } = "";
    public string? PreviousName { get; set; }
    public string? NodeId { get; set; }
    public List<ChainElementDto> Elements { get; set; } = new();
}

internal sealed class CatalogDto
{
    public List<CatalogItemDto> Apps { get; set; } = new();
    public List<CatalogItemDto> Outputs { get; set; } = new();
    public List<CatalogItemDto> Inputs { get; set; } = new();
    public List<CatalogItemDto> Virtuals { get; set; } = new();
    public List<CatalogItemDto> Effects { get; set; } = new();
    public List<CatalogItemDto> Chains { get; set; } = new();
    public string DefaultOutputId { get; set; } = "";
    public string DefaultOutputName { get; set; } = "";
}

internal sealed class ParameterDto
{
    public int ProcessorIndex { get; set; }
    public int ParameterIndex { get; set; }
    public string Name { get; set; } = "";
    public float Min { get; set; }
    public float Max { get; set; }
    public float Value { get; set; }
    public string Unit { get; set; } = "";
    public bool Logarithmic { get; set; }
    public bool IsToggle { get; set; }
    public bool IsInteger { get; set; }
    public bool IsChoice { get; set; }
    public string[] Options { get; set; } = Array.Empty<string>();
}

internal sealed class NodeDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Flavor { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Accent { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public float Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public string? MicHint { get; set; }
    public string? DeviceId { get; set; }
    public List<ParameterDto> Parameters { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool IsRecorder { get; set; }
    public string RecordingPath { get; set; } = "";
    public string RecordingFormat { get; set; } = "Wav24";
    public string RecorderMode { get; set; } = "Off";
    public bool RecorderAutoSave { get; set; }
    public bool IsWaveform { get; set; }
    public bool IsDbMeter { get; set; }
    public bool IsTranscriber { get; set; }
    public string? TranscriberModel { get; set; }
    public string? ChainName { get; set; }
    public List<string> ChainElements { get; set; } = new();
    public bool DuplicateRisk { get; set; }
    public string? DuplicateWarning { get; set; }
    public string? WarningSeverity { get; set; }
}

internal sealed class LinkDto
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = "";
    public float Gain { get; set; } = 1;
}

internal sealed class FrameDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

internal sealed class FramesPayload
{
    public List<FrameDto> Frames { get; set; } = new();
}

internal sealed class GraphDto
{
    public int Version { get; set; }
    public List<NodeDto> Nodes { get; set; } = new();
    public List<LinkDto> Links { get; set; } = new();
    public List<FrameDto> Frames { get; set; } = new();
    public List<GraphWarningDto> Warnings { get; set; } = new();
    public bool Running { get; set; }
}

internal sealed class GraphWarningDto
{
    public string SourceId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "warning";
}

internal sealed class FirstRouteDto
{
    public string SourceId { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public string OutputTitle { get; set; } = "";
    public string? Warning { get; set; }
    public string? Severity { get; set; }
}

internal sealed class NodeTelemetryDto
{
    public float Level { get; set; }
    public bool Clipping { get; set; }
    public string? RecordingStatus { get; set; }
    public double? RecordingSeconds { get; set; }
    public string? RecordingError { get; set; }
    public float[]? Waveform { get; set; }
    public float? Db { get; set; }
    public float? PeakDb { get; set; }
    public bool? DbMeterVertical { get; set; }
    public string? TranscriptionStatus { get; set; }
    public string? Transcript { get; set; }
    public string? PartialTranscript { get; set; }
    public string? TranscriptionError { get; set; }
    public float TranscriptionLevel { get; set; }
    public bool TranscriptionHasAudio { get; set; }
    public double? TranscriptionElapsed { get; set; }
}

internal sealed class TelemetryDto
{
    public Dictionary<string, NodeTelemetryDto> Meters { get; set; } = new();
    public bool Running { get; set; }
    public bool Clipping { get; set; }
    public float MasterGain { get; set; }
    public bool MasterMuted { get; set; }
    public bool MicMuted { get; set; }
    public DiagnosticsDto? Diagnostics { get; set; }
}

internal sealed class DiagnosticsDto
{
    public double CpuPercent { get; set; }
    public double CpuShare { get; set; }
    public double? GpuPercent { get; set; }
    public int RenderTier { get; set; }
    public double WorkingSetMb { get; set; }
    public double ManagedMb { get; set; }
    public int Threads { get; set; }
    public double ProcessUptimeSeconds { get; set; }
    public int NodeCount { get; set; }
    public int LinkCount { get; set; }
    public int SourceCount { get; set; }
    public int SourcesRunning { get; set; }
    public double WorstBacklogMs { get; set; }
    public long SkippedFrames { get; set; }
    public int OutputCount { get; set; }
    public int OutputsRunning { get; set; }
    public double WorstLatencyMs { get; set; }
    public double ChainLatencyMs { get; set; }
    public long ShortReadFrames { get; set; }
    public bool LowLatency { get; set; }
    public double TelemetryMs { get; set; }
    public string AudioFormat { get; set; } = "48 kHz · stereo · f32";
}

internal sealed class SettingsDto
{
    public bool AutoLoadSession { get; set; } = true;
    public bool FollowPlayingApps { get; set; }
    public string FollowOutputId { get; set; } = "";
    public string UiLanguage { get; set; } = "en";
    public bool StartRoutingOnLaunch { get; set; }
    public bool HighlightMutedNodes { get; set; } = true;
    public bool SignalGlow { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public bool HighlightCableOnHover { get; set; } = true;
    public bool CableAnimation { get; set; } = true;
    public int MinimapMode { get; set; } = 2;
    public bool StartWithWindows { get; set; }
    public bool ShowDiagnostics { get; set; }
    public string TranscriberModel { get; set; } = "";
    public List<TranscriberModelDto> TranscriberModels { get; set; } = new();
    public TranscriberDownloadDto? TranscriberDownload { get; set; }
    public string InstalledModelsSize { get; set; } = "";
    public int InstalledModelsCount { get; set; }
    public string AppVersion { get; set; } = "";
    public string DotNetVersion { get; set; } = "";
    public string WebView2Version { get; set; } = "";
    public int RenderTier { get; set; }
    public string DataFolder { get; set; } = "";
    public string ModelsFolder { get; set; } = "";
}

internal sealed class TranscriberModelDto
{
    public string Id { get; set; } = "";
    public string Language { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
    public int SizeMb { get; set; }
    public bool Installed { get; set; }
    public string InstalledSize { get; set; } = "";
    public string? Note { get; set; }
}

internal sealed class TranscriberDownloadDto
{
    public string Id { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Percent { get; set; }
    public string Message { get; set; } = "";
}

internal sealed class StatusDto
{
    public string Text { get; set; } = "";
    public bool IsError { get; set; }
}

internal sealed class HistoryDto
{
    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }
    public string UndoLabel { get; set; } = "";
    public string RedoLabel { get; set; } = "";
}

internal sealed class BridgeMessage
{
    public string Type { get; set; } = "";
    public JsonElement Payload { get; set; }
}
