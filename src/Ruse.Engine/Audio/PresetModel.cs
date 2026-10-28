using System.Text.Json;

namespace Ruse.App.Audio;

/// <summary>
/// Serializable snapshot of a routing graph. Positions are stored so the
/// canvas looks identical after a reload.
/// </summary>
public sealed class Preset
{
    public string Name { get; set; } = "Preset";
    public List<PresetSource> Sources { get; set; } = new();
    public List<PresetOutput> Outputs { get; set; } = new();
    public List<PresetRoute> Routes { get; set; } = new();

    /// <summary>Effect-chain nodes placed on the canvas.</summary>
    public List<PresetModifier> Modifiers { get; set; } = new();

    /// <summary>Background group frames used to organize the canvas.</summary>
    public List<PresetFrame> Frames { get; set; } = new();

    /// <summary>source â†’ modifier links, by index.</summary>
    public List<PresetSourceModifierLink> SourceModifiers { get; set; } = new();

    /// <summary>modifier â†’ output links, by index.</summary>
    public List<PresetModifierOutputLink> ModifierOutputs { get; set; } = new();

    /// <summary>modifier â†’ modifier links, by index.</summary>
    public List<PresetModifierLink> ModifierLinks { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static Preset FromJson(string json) =>
        JsonSerializer.Deserialize<Preset>(json, Options) ?? new Preset();
}

public sealed class PresetSource
{
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "Application";
    public uint? ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? DeviceId { get; set; }
    public string? FilePath { get; set; }
    public bool FileLoop { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; }
}

public sealed class PresetOutput
{
    public string DeviceId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsVirtual { get; set; }
    public string? MicHint { get; set; }
    public bool IsFile { get; set; }
    public string? FilePath { get; set; }
    public string? FileFormat { get; set; }
    public string RecordingMode { get; set; } = "Off";
    public bool AutoSave { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; }
}

/// <summary>Route by index into Sources/Outputs lists.</summary>
public sealed class PresetRoute
{
    public int SourceIndex { get; set; }
    public int OutputIndex { get; set; }
    public float Gain { get; set; } = 1.0f;
}

/// <summary>A modifier node placed on the canvas, with its effect chain.</summary>
public sealed class PresetModifier
{
    public string Title { get; set; } = "Modifier";
    public bool Enabled { get; set; } = true;
    public string? TranscriberModel { get; set; }
    public string? TranslatorTarget { get; set; }
    public string? TranslatorModel { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public List<PresetProcessor> Processors { get; set; } = new();
}

/// <summary>A named background frame used to group canvas nodes.</summary>
public sealed class PresetFrame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Group";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>One effect in a modifier's chain.</summary>
public sealed class PresetProcessor
{
    public string TypeId { get; set; } = "";
    public Dictionary<string, float> Parameters { get; set; } = new();

    /// <summary>For a VST3 processor: the bundle/class identifier it was loaded from.</summary>
    public string? PluginPath { get; set; }

    /// <summary>For a VST3 processor: the base64 component state chunk.</summary>
    public string? PluginState { get; set; }

    /// <summary>For a saved chain: the effects it contains, in order.</summary>
    public List<PresetProcessor>? Elements { get; set; }

    /// <summary>For a saved chain: its name.</summary>
    public string? Name { get; set; }
}

public sealed class PresetSourceModifierLink
{
    public int SourceIndex { get; set; }
    public int ModifierIndex { get; set; }
}

public sealed class PresetModifierOutputLink
{
    public int ModifierIndex { get; set; }
    public int OutputIndex { get; set; }
}

public sealed class PresetModifierLink
{
    public int FromModifierIndex { get; set; }
    public int ToModifierIndex { get; set; }
}

/// <summary>
/// Converts live effects to and from their preset representation. Shared by the
/// UI and the preset round-trip test so both exercise the same code.
/// </summary>
public static class PresetSerializer
{
    /// <summary>Captures an effect and its current parameter values.</summary>
    public static PresetProcessor SerializeProcessor(IAudioProcessor processor)
    {
        if (processor is Vst3Processor vst)
        {
            return new PresetProcessor
            {
                TypeId = Vst3Processor.Id,
                Name = vst.DisplayName,
                PluginPath = vst.Identifier,
                PluginState = vst.CaptureStateBase64(),
                Parameters = vst.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Get()),
            };
        }

        if (processor is ChainProcessor chain)
        {
            return new PresetProcessor
            {
                TypeId = chain.TypeId,
                Name = chain.DisplayName,
                Elements = chain.Processors.Select(SerializeProcessor).ToList(),
            };
        }

        var entry = new PresetProcessor { TypeId = processor.TypeId };
        if (processor is IParameterizedProcessor parameterized)
        {
            foreach (var parameter in parameterized.Parameters)
            {
                entry.Parameters[parameter.Name] = parameter.Get();
            }
        }

        return entry;
    }

    /// <summary>
    /// Recreates an effect and applies its saved parameters. Returns null when
    /// the type is no longer registered (e.g. a preset from a newer build).
    /// </summary>
    public static IAudioProcessor? CreateProcessor(PresetProcessor entry)
    {
        if (entry.TypeId == Vst3Processor.Id)
        {
            if (string.IsNullOrWhiteSpace(entry.PluginPath))
            {
                return null;
            }

            return new Vst3Processor(entry.PluginPath, entry.PluginState, entry.Parameters);
        }

        if (entry.TypeId == "chain")
        {
            var chain = new ChainProcessor(entry.Name ?? "Chain");
            if (entry.Elements != null)
            {
                foreach (var inner in entry.Elements)
                {
                    var processor = CreateProcessor(inner);
                    if (processor != null && processor is not ITapProcessor && processor is not Vst3Processor)
                    {
                        chain.Add(processor);
                    }
                }
            }

            return chain;
        }

        var created = AudioProcessorCatalog.Create(entry.TypeId);
        if (created is IParameterizedProcessor parameterized)
        {
            foreach (var (name, value) in entry.Parameters)
            {
                var parameter = parameterized.Parameters.FirstOrDefault(p => p.Name == name);
                parameter?.Set(value);
            }
        }

        return created;
    }
}
