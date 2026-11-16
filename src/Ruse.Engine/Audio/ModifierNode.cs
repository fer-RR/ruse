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

    /// <summary>True for the timeline-style mixer node.</summary>
    public bool IsMixer { get; set; }

    /// <summary>Stereo width of the summed mixer bus (0 collapsed, 1 normal, 2 wide).</summary>
    public float MixerWidth
    {
        get => _mixerWidth;
        set => _mixerWidth = Math.Clamp(value, 0f, 2f);
    }

    private volatile float _mixerWidth = 1f;

    /// <summary>Equal-power normalizes the summed mixer bus by the audible input count.</summary>
    public bool MixerAutoGain
    {
        get => _mixerAutoGain;
        set => _mixerAutoGain = value;
    }

    private volatile bool _mixerAutoGain;

    /// <summary>0 = A only, 0.5 = equal A/B, 1 = B only.</summary>
    public float MixerCrossfade
    {
        get => _mixerCrossfade;
        set => _mixerCrossfade = Math.Clamp(value, 0f, 1f);
    }

    private volatile float _mixerCrossfade;

    public const int MixerMaxDelayMs = 500;

    private volatile float _mixerMeterDbLeft = -60f;
    private volatile float _mixerMeterDbRight = -60f;
    private volatile float _mixerMeterPeakDbLeft = -60f;
    private volatile float _mixerMeterPeakDbRight = -60f;
    private volatile bool _mixerMeterClipping;
    private long _mixerPeakHoldUntilLeft;
    private long _mixerPeakHoldUntilRight;

    public float MixerMeterDbLeft => _mixerMeterDbLeft;
    public float MixerMeterDbRight => _mixerMeterDbRight;
    public float MixerMeterPeakDbLeft => _mixerMeterPeakDbLeft;
    public float MixerMeterPeakDbRight => _mixerMeterPeakDbRight;
    public bool MixerMeterClipping => _mixerMeterClipping;

    internal void UpdateMixerMeter(ReadOnlySpan<float> block, int frames)
    {
        if (!IsMixer || frames <= 0)
        {
            return;
        }

        int measuredFrames = Math.Min(frames, block.Length / 2);
        if (measuredFrames < 1)
        {
            return;
        }

        double sumLeft = 0;
        double sumRight = 0;
        float peakLeft = 0;
        float peakRight = 0;
        for (int frame = 0; frame < measuredFrames; frame++)
        {
            int index = frame * 2;
            float left = block[index];
            float right = block[index + 1];
            sumLeft += left * left;
            sumRight += right * right;
            peakLeft = Math.Max(peakLeft, Math.Abs(left));
            peakRight = Math.Max(peakRight, Math.Abs(right));
        }

        float dbLeft = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumLeft / measuredFrames))));
        float dbRight = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumRight / measuredFrames))));
        float peakDbLeft = (float)(20.0 * Math.Log10(Math.Max(1e-6, peakLeft)));
        float peakDbRight = (float)(20.0 * Math.Log10(Math.Max(1e-6, peakRight)));
        long now = Environment.TickCount64;

        _mixerMeterDbLeft = Math.Max(-60f, dbLeft);
        _mixerMeterDbRight = Math.Max(-60f, dbRight);
        if (peakDbLeft >= _mixerMeterPeakDbLeft)
        {
            _mixerMeterPeakDbLeft = peakDbLeft;
            _mixerPeakHoldUntilLeft = now + 1500;
        }
        else if (now >= _mixerPeakHoldUntilLeft)
        {
            _mixerMeterPeakDbLeft = Math.Max(-60f, peakDbLeft);
        }

        if (peakDbRight >= _mixerMeterPeakDbRight)
        {
            _mixerMeterPeakDbRight = peakDbRight;
            _mixerPeakHoldUntilRight = now + 1500;
        }
        else if (now >= _mixerPeakHoldUntilRight)
        {
            _mixerMeterPeakDbRight = Math.Max(-60f, peakDbRight);
        }

        if (peakLeft > 1f || peakRight > 1f)
        {
            _mixerMeterClipping = true;
        }
    }

    public void ResetMixerMeter()
    {
        _mixerMeterDbLeft = -60f;
        _mixerMeterDbRight = -60f;
        _mixerMeterPeakDbLeft = -60f;
        _mixerMeterPeakDbRight = -60f;
        _mixerMeterClipping = false;
        _mixerPeakHoldUntilLeft = 0;
        _mixerPeakHoldUntilRight = 0;
    }

    public Dictionary<SourceNode, MixerInputSettings> MixerSourceInputs { get; } = new();

    public Dictionary<ModifierNode, MixerInputSettings> MixerModifierInputs { get; } = new();

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

    public sealed class MixerInputSettings
    {
        public float Gain { get; set; } = 1f;
        public float Pan { get; set; }
        public float DelayMs { get; set; }
        public bool Muted { get; set; }
        public bool Solo { get; set; }
        public bool BusB { get; set; }
        public bool Inverted { get; set; }
    }
}
