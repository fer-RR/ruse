using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruse.App.Audio;

/// <summary>One effect inside a saved chain.</summary>
public sealed class SubgraphElement
{
    public string TypeId { get; set; } = "";

    public Dictionary<string, float> Parameters { get; set; } = new();
}

/// <summary>
/// A named chain of effects that can be dropped on the canvas as a single node
/// and reused anywhere - the "nerd" version of a subgraph.
/// </summary>
public sealed class SubgraphDefinition
{
    public string Name { get; set; } = "Chain";

    /// <summary>The effects, in processing order.</summary>
    public List<SubgraphElement> Elements { get; set; } = new();

    /// <summary>Short summary shown in the sidebar.</summary>
    public string Description
    {
        get
        {
            if (Elements.Count == 0)
            {
                return "empty chain";
            }

            var names = Elements.Select(e =>
                AudioProcessorCatalog.Find(e.TypeId)?.DisplayName ?? e.TypeId);
            return string.Join(" → ", names);
        }
    }

    /// <summary>Creates the live processors for this chain.</summary>
    public ChainProcessor CreateProcessor()
    {
        var processor = new ChainProcessor(Name);
        foreach (var element in Elements)
        {
            var instance = AudioProcessorCatalog.Create(element.TypeId);
            if (instance == null)
            {
                continue;
            }

            if (instance is IParameterizedProcessor parameterized)
            {
                foreach (var (name, value) in element.Parameters)
                {
                    var parameter = parameterized.Parameters.FirstOrDefault(p => p.Name == name);
                    parameter?.Set(value);
                }
            }

            processor.Add(instance);
        }

        return processor;
    }

    /// <summary>Captures the current state of a chain processor.</summary>
    public static SubgraphDefinition FromProcessor(ChainProcessor processor)
    {
        var definition = new SubgraphDefinition { Name = processor.DisplayName };
        foreach (var inner in processor.Processors)
        {
            var element = new SubgraphElement { TypeId = inner.TypeId };
            if (inner is IParameterizedProcessor parameterized)
            {
                foreach (var parameter in parameterized.Parameters)
                {
                    element.Parameters[parameter.Name] = parameter.Get();
                }
            }

            definition.Elements.Add(element);
        }

        return definition;
    }
}

/// <summary>
/// Runs several effects in sequence as if they were one - the audio
/// equivalent of a Blender node group or a Node-RED subflow.
/// </summary>
public sealed class ChainProcessor : IAudioProcessor
{
    private readonly List<IAudioProcessor> _processors = new();

    public ChainProcessor(string name) => DisplayName = name;

    public string TypeId => "chain";

    public string DisplayName { get; set; }

    /// <summary>The effects inside the chain, in order.</summary>
    public IReadOnlyList<IAudioProcessor> Processors => _processors;

    public void Add(IAudioProcessor processor) => _processors.Add(processor);

    public void Process(Span<float> samples, int frames)
    {
        for (int i = 0; i < _processors.Count; i++)
        {
            _processors[i].Process(samples, frames);
        }
    }

    public void Reset()
    {
        foreach (var processor in _processors)
        {
            processor.Reset();
        }
    }

    public int LatencySamples
    {
        get
        {
            int total = 0;
            foreach (var processor in _processors)
            {
                total += processor.LatencySamples;
            }

            return total;
        }
    }
}

/// <summary>The user's saved chains, stored next to the other settings.</summary>
public static class SubgraphLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string LibraryPath { get; } = Path.Combine(AppSettings.Directory, "chains.json");

    private static readonly List<SubgraphDefinition> _chains = new();

    /// <summary>The saved chains.</summary>
    public static IReadOnlyList<SubgraphDefinition> Chains => _chains;

    public static void Load()
    {
        _chains.Clear();
        try
        {
            if (!File.Exists(LibraryPath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<SubgraphDefinition>>(
                File.ReadAllText(LibraryPath), Options);
            if (loaded != null)
            {
                _chains.AddRange(loaded);
            }
        }
        catch
        {
            // A broken library must never stop the app from starting.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(_chains, Options));
        }
        catch
        {
        }
    }

    /// <summary>Adds or replaces a chain by name.</summary>
    public static void Store(SubgraphDefinition definition)
    {
        for (int i = 0; i < _chains.Count; i++)
        {
            if (string.Equals(_chains[i].Name, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                _chains[i] = definition;
                Save();
                return;
            }
        }

        _chains.Add(definition);
        Save();
    }

    public static void Remove(SubgraphDefinition definition)
    {
        _chains.Remove(definition);
        Save();
    }
}
