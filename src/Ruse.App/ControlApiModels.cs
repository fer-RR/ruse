namespace Ruse.App;

internal sealed class ControlStateDto
{
    public bool Running { get; set; }
    public int NodeCount { get; set; }
    public int LinkCount { get; set; }
    public string Version { get; set; } = "";
    public string Status { get; set; } = "";
}

internal sealed class ControlNodeDto
{
    public string Id { get; set; } = "";
    public string ControlId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Flavor { get; set; } = "";
    public float Gain { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public bool Enabled { get; set; } = true;
    public List<ControlProcessorDto> Processors { get; set; } = new();
}

internal sealed class ControlProcessorDto
{
    public int Index { get; set; }
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ControlParameterDto> Parameters { get; set; } = new();
}

internal sealed class ControlParameterDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public float Min { get; set; }
    public float Max { get; set; }
    public float Value { get; set; }
    public float Normalized { get; set; }
    public string Unit { get; set; } = "";
    public bool IsToggle { get; set; }
    public bool IsInteger { get; set; }
    public bool IsChoice { get; set; }
    public string[] Options { get; set; } = Array.Empty<string>();
}

internal sealed class ControlTelemetryDto
{
    public bool Running { get; set; }
    public List<ControlTelemetryNodeDto> Nodes { get; set; } = new();
}

internal sealed class ControlTelemetryNodeDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public float Level { get; set; }
    public float LevelLeft { get; set; }
    public float LevelRight { get; set; }
    public bool Clipping { get; set; }
    public bool Muted { get; set; }
}

internal sealed class ControlSetParameterRequest
{
    public float? Value { get; set; }
    public float? Normalized { get; set; }
}

internal sealed class ControlNodePatchRequest
{
    public float? Gain { get; set; }
    public bool? Muted { get; set; }
    public bool? Solo { get; set; }
    public bool? Enabled { get; set; }
    public string? ControlId { get; set; }
}

internal sealed class ControlTransportRequest
{
    public bool Running { get; set; }
}

internal sealed class ControlWsRequest
{
    public string Type { get; set; } = "";
    public int? Id { get; set; }
    public string? NodeId { get; set; }
    public int? Processor { get; set; }
    public int? Parameter { get; set; }
    public double? Value { get; set; }
    public double? Normalized { get; set; }
    public double? Gain { get; set; }
    public bool? Muted { get; set; }
    public bool? Solo { get; set; }
    public bool? Enabled { get; set; }
    public string? ControlId { get; set; }
    public bool? Running { get; set; }
}
