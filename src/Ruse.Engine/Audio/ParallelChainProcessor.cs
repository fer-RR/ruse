namespace Ruse.App.Audio;

/// <summary>
/// Runs an effect chain in parallel with the dry signal. This is the routing
/// primitive behind a send/return: the chain sees a copy of the block and its
/// output is added back at the send amount.
/// </summary>
public sealed class ParallelChainProcessor : IAudioProcessor, IParameterizedProcessor, IDisposable
{
    public const string Id = "parallel";

    private readonly IAudioProcessor[] _processors;
    private readonly AudioParameter[] _parameters;
    private float[] _dry = new float[8192];
    private volatile float _send;
    private float _currentSend;
    private bool _primed;

    public ParallelChainProcessor(IAudioProcessor[] processors, float send)
    {
        _processors = processors;
        _send = Math.Clamp(send, 0f, 1f);
        _currentSend = _send;
        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Send",
                Min = 0f,
                Max = 1f,
                Default = 0.5f,
                Get = () => _send,
                Set = value => _send = Math.Clamp(value, 0f, 1f),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Parallel send";
    public IReadOnlyList<IAudioProcessor> Processors => _processors;
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public float Send
    {
        get => _send;
        set => _send = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// The dry path is immediate, so reporting the wet latency here would make
    /// the route aligner delay unrelated routes. The wet tail is intentionally
    /// allowed to lag behind the dry signal, as on a mixer send.
    /// </summary>
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (_processors.Length == 0 || samples.IsEmpty)
        {
            return;
        }

        float targetSend = _send;
        if (!_primed)
        {
            _currentSend = targetSend;
            _primed = true;
        }

        if (_dry.Length < samples.Length)
        {
            _dry = new float[Math.Max(samples.Length, _dry.Length * 2)];
        }

        samples.CopyTo(_dry);
        for (int i = 0; i < _processors.Length; i++)
        {
            _processors[i].Process(samples, frames);
        }

        for (int i = 0; i < samples.Length; i++)
        {
            _currentSend = ParameterSmoothing.Approach(_currentSend, targetSend, ParameterSmoothing.FastAlpha);
            samples[i] = _dry[i] + (samples[i] * _currentSend);
        }
    }

    public void Reset()
    {
        _primed = false;
        for (int i = 0; i < _processors.Length; i++)
        {
            _processors[i].Reset();
        }
    }

    public void Dispose()
    {
        var seen = new HashSet<IAudioProcessor>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _processors.Length; i++)
        {
            var processor = _processors[i];
            if (processor is ISharedProcessor || !seen.Add(processor))
            {
                continue;
            }

            (processor as IDisposable)?.Dispose();
        }
    }
}
