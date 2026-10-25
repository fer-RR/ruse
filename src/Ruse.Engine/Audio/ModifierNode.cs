namespace Ruse.App.Audio;

/// <summary>
/// A node that sits on a route between a <see cref="SourceNode"/> and an
/// <see cref="OutputNode"/> and applies an ordered chain of
/// <see cref="IAudioProcessor"/> effects to the signal passing through it.
/// </summary>
/// <remarks>
/// A modifier does not own audio buffers or threads; it is a description of an
/// effect chain. <see cref="AudioGraph"/> walks the chain when rebuilding a
/// route's <see cref="OutputNode.Route.Processors"/> array, flattening every
/// modifier between a source and an output into one immutable snapshot.
/// </remarks>
public sealed class ModifierNode
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; }

    /// <summary>False bypasses this modifier's effects while keeping its routes.</summary>
    public bool Enabled { get; set; } = true;

    private readonly List<IAudioProcessor> _processors = new();

    /// <summary>Sources feeding this modifier.</summary>
    public List<SourceNode> Inputs { get; } = new();

    /// <summary>Outputs this modifier feeds.</summary>
    public List<OutputNode> Outputs { get; } = new();

    /// <summary>Modifiers chained after this one (modifier â†’ modifier).</summary>
    public List<ModifierNode> Next { get; } = new();

    public int Count => _processors.Count;

    public ModifierNode(string title)
    {
        Title = title;
    }

    /// <summary>Creates a modifier pre-populated with the first registered effect.</summary>
    public static ModifierNode CreateDefault()
    {
        var node = new ModifierNode("Modifier");
        var first = AudioProcessorCatalog.All.FirstOrDefault();
        if (first != null)
        {
            node.Add(first.Create());
        }

        return node;
    }

    /// <summary>Appends an effect to the end of the chain.</summary>
    public void Add(IAudioProcessor processor)
    {
        _processors.Add(processor);
    }

    public void Remove(IAudioProcessor processor)
    {
        _processors.Remove(processor);
    }

    /// <summary>Removes every effect from the chain (used when restoring a preset).</summary>
    public void Clear()
    {
        _processors.Clear();
    }

    public IReadOnlyList<IAudioProcessor> Processors => _processors;

    /// <summary>Returns the chain in processing order as a stable snapshot.</summary>
    public IAudioProcessor[] Snapshot() => _processors.ToArray();

    public void ResetAll()
    {
        foreach (var p in _processors)
        {
            p.Reset();
        }
    }
}
