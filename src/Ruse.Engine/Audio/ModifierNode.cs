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
public sealed class ModifierNode : IDisposable
{
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; }

    /// <summary>False bypasses this modifier's effects while keeping its routes.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private volatile bool _enabled = true;

    /// <summary>Runs the effect chain on a copy and mixes it back with the dry signal.</summary>
    public bool Parallel { get; set; }

    /// <summary>Wet amount used when <see cref="Parallel"/> is enabled.</summary>
    public float Send { get; set; } = 0.5f;

    /// <summary>Cached parallel wrapper, rebuilt when the chain changes.</summary>
    public ParallelChainProcessor? ParallelChain { get; set; }

    private readonly List<IAudioProcessor> _processors = new();

    /// <summary>Sources feeding this modifier.</summary>
    public List<SourceNode> Inputs { get; } = new();

    /// <summary>Sources that drive a sidechain/key input without being mixed audibly.</summary>
    public List<SourceNode> SidechainInputs { get; } = new();

    /// <summary>Upstream modifiers that drive a sidechain/key input.</summary>
    public List<ModifierNode> SidechainModifiers { get; } = new();

    /// <summary>Outputs this modifier feeds.</summary>
    public List<OutputNode> Outputs { get; } = new();

    /// <summary>Per-output send gain for this modifier.</summary>
    public Dictionary<OutputNode, float> OutputGains { get; } = new();

    /// <summary>Modifiers chained after this one (modifier â†’ modifier).</summary>
    public List<ModifierNode> Next { get; } = new();

    public int Count => _processors.Count;

    public ModifierNode(string title)
    {
        Title = title;
    }

    /// <summary>Appends an effect to the end of the chain.</summary>
    public void Add(IAudioProcessor processor)
    {
        _processors.Add(processor);
        ParallelChain = null;
    }

    public void Remove(IAudioProcessor processor)
    {
        _processors.Remove(processor);
        ParallelChain = null;
    }

    /// <summary>Removes every effect from the chain (used when restoring a preset).</summary>
    public void Clear()
    {
        _processors.Clear();
        ParallelChain = null;
    }

    public IReadOnlyList<IAudioProcessor> Processors => _processors;

    /// <summary>Returns the chain in processing order as a stable snapshot.</summary>
    public IAudioProcessor[] Snapshot() => _processors.ToArray();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ParallelChain = null;
        var seen = new HashSet<IAudioProcessor>(ReferenceEqualityComparer.Instance);
        foreach (var processor in _processors)
        {
            if (seen.Add(processor))
            {
                (processor as IDisposable)?.Dispose();
            }
        }

        _processors.Clear();
    }
}
