using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Runs an in-process VST3 on a worker thread and bypasses it if a block stops
/// returning. The plugin can still crash the process, so isolation remains the
/// default; this guard is the fallback when isolation is disabled or unavailable.
/// </summary>
internal sealed class GuardedVst3Processor : IVst3Processor
{
    private const int MaxFrames = 2048;
    private const int Channels = 2;
    private const int SlotCount = 4;
    private const int StallMs = 250;

    private readonly IVst3Processor _inner;
    private readonly AudioSlot[] _slots = CreateSlots();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly Thread _watchdog;
    private int _freeSlots = (1 << SlotCount) - 1;
    private AudioSlot? _pending;
    private AudioSlot? _ready;
    private long _epoch;
    private long _callStarted;
    private int _faulted;
    private int _disposed;
    private string? _stallError;
    private Action? _changed;
    private volatile bool _stop;

    public GuardedVst3Processor(IVst3Processor inner)
    {
        _inner = inner;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "VST3 in-process audio",
            Priority = ThreadPriority.AboveNormal,
        };
        _watchdog = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "VST3 in-process watchdog",
        };
        _worker.Start();
        _watchdog.Start();
    }

    public event Action? Changed
    {
        add
        {
            _changed += value;
            _inner.Changed += value;
        }
        remove
        {
            _changed -= value;
            _inner.Changed -= value;
        }
    }

    public string TypeId => _inner.TypeId;
    public string DisplayName => _inner.DisplayName;
    public string Identifier => _inner.Identifier;
    public string PluginName => _inner.PluginName;
    public string Vendor => _inner.Vendor;
    public string Version => _inner.Version;
    public string Status => Volatile.Read(ref _faulted) != 0
        ? "Plugin stopped responding; bypassed"
        : _inner.Status;
    public string? Error => Volatile.Read(ref _faulted) != 0 ? _stallError : _inner.Error;
    public bool IsReady => Volatile.Read(ref _faulted) == 0 && _inner.IsReady;
    public bool HasEditor => _inner.HasEditor;
    public bool IsEditorOpen => _inner.IsEditorOpen;
    public IReadOnlyList<AudioParameter> Parameters => _inner.Parameters;
    public int LatencySamples => _inner.IsReady ? _inner.LatencySamples + AudioEngineConfig.BlockFrames : 0;

    public void Start() => _inner.Start();

    public string? CaptureStateBase64() => _inner.CaptureStateBase64();

    public bool OpenEditor(string title) => _inner.OpenEditor(title);

    public void CloseEditor() => _inner.CloseEditor();

    public void Reset()
    {
        Interlocked.Increment(ref _epoch);
        ClearBuffers();
        _inner.Reset();
    }

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || frames > MaxFrames || samples.Length < frames * Channels ||
            !IsReady || _stop)
        {
            return;
        }

        int validSamples = frames * Channels;
        AudioSlot? ready = Interlocked.Exchange(ref _ready, null);
        if (ready != null)
        {
            if (ready.Epoch == Volatile.Read(ref _epoch))
            {
                int readySamples = Math.Min(validSamples, ready.Frames * Channels);
                ready.Samples.AsSpan(0, readySamples).CopyTo(samples);
                if (readySamples < validSamples)
                {
                    samples[readySamples..validSamples].Clear();
                }
            }

            ReturnSlot(ready);
        }

        AudioSlot? pending = RentSlot();
        if (pending == null)
        {
            return;
        }

        samples[..validSamples].CopyTo(pending.Samples);
        pending.Frames = frames;
        pending.Epoch = Volatile.Read(ref _epoch);

        AudioSlot? dropped = Interlocked.Exchange(ref _pending, pending);
        if (dropped != null)
        {
            ReturnSlot(dropped);
        }

        _wake.Set();
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            if (!_wake.WaitOne(20))
            {
                continue;
            }

            AudioSlot? pending = Interlocked.Exchange(ref _pending, null);
            if (pending == null)
            {
                continue;
            }

            if (_stop || Volatile.Read(ref _faulted) != 0)
            {
                ReturnSlot(pending);
                continue;
            }

            Interlocked.Exchange(ref _callStarted, Environment.TickCount64);
            try
            {
                _inner.Process(
                    pending.Samples.AsSpan(0, pending.Frames * Channels),
                    pending.Frames);

                if (!_stop && Volatile.Read(ref _faulted) == 0 &&
                    pending.Epoch == Volatile.Read(ref _epoch))
                {
                    AudioSlot? dropped = Interlocked.Exchange(ref _ready, pending);
                    if (dropped != null)
                    {
                        ReturnSlot(dropped);
                    }

                    continue;
                }
            }
            catch (Exception ex)
            {
                Fault("The in-process VST3 plugin failed; bypassing it.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _callStarted, 0);
            }

            ReturnSlot(pending);
        }
    }

    private void WatchdogLoop()
    {
        while (!_stop)
        {
            Thread.Sleep(25);
            long started = Interlocked.Read(ref _callStarted);
            if (started != 0 && Environment.TickCount64 - started > StallMs)
            {
                Fault(
                    "The in-process VST3 plugin stopped responding; bypassing it.",
                    new TimeoutException($"The plugin did not finish a block within {StallMs} ms."));
            }
        }
    }

    private void Fault(string message, Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _stallError = message;
        Interlocked.Increment(ref _epoch);
        ClearBuffers();
        AudioFaultLog.ReportEffectFault(DisplayName, exception);
        try
        {
            _changed?.Invoke();
        }
        catch
        {
        }
    }

    private void ClearBuffers()
    {
        AudioSlot? pending = Interlocked.Exchange(ref _pending, null);
        if (pending != null)
        {
            ReturnSlot(pending);
        }

        AudioSlot? ready = Interlocked.Exchange(ref _ready, null);
        if (ready != null)
        {
            ReturnSlot(ready);
        }
    }

    private AudioSlot? RentSlot()
    {
        while (true)
        {
            int mask = Volatile.Read(ref _freeSlots);
            if (mask == 0)
            {
                return null;
            }

            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            int next = mask & ~(1 << index);
            if (Interlocked.CompareExchange(ref _freeSlots, next, mask) == mask)
            {
                return _slots[index];
            }
        }
    }

    private void ReturnSlot(AudioSlot slot) =>
        Interlocked.Or(ref _freeSlots, 1 << slot.Index);

    private static AudioSlot[] CreateSlots()
    {
        var slots = new AudioSlot[SlotCount];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new AudioSlot(i);
        }

        return slots;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop = true;
        _wake.Set();
        bool workerStopped = _worker.Join(500);
        _watchdog.Join(500);
        ClearBuffers();
        if (workerStopped)
        {
            _wake.Dispose();
        }

        if (!workerStopped || Interlocked.Read(ref _callStarted) != 0)
        {
            AppLog.Warning($"The in-process VST3 '{PluginName}' is still inside a native call; leaving it allocated.");
            return;
        }

        _inner.Dispose();
    }

    private sealed class AudioSlot
    {
        public AudioSlot(int index) => Index = index;

        public int Index { get; }
        public float[] Samples { get; } = new float[MaxFrames * Channels];
        public int Frames;
        public long Epoch;
    }
}
