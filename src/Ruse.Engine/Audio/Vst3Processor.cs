using OwnVST3Host;
using OwnVST3Host.NativeWindow;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Hosts one VST3 effect. The native wrapper owns the plugin thread; this class
/// adapts the plugin to the engine's stereo interleaved processing contract.
/// </summary>
public sealed class Vst3Processor : IVst3Processor
{
    public const string Id = "vst3";
    private const int MaxBlockFrames = 1024;
    private const int MaxMidiEvents = 128;

    private readonly string _identifier;
    private readonly byte[]? _initialState;
    private readonly IReadOnlyDictionary<string, float>? _pendingParameters;
    private readonly bool _allowInstrument;
    private readonly object _audioLock = new();
    private readonly object _stateLock = new();
    private readonly object _pollLock = new();
    private readonly Vst3MidiQueue _midi = new();
    private readonly Vst3MidiEvent[] _midiDrain = new Vst3MidiEvent[MaxMidiEvents];
    private readonly MidiEvent[]?[] _midiBuffers = new MidiEvent[MaxMidiEvents + 1][];

    private ThreadedVst3Wrapper? _wrapper;
    private VstEditorController? _editor;
    private CancellationTokenSource? _pollCancellation;
    private AudioParameter[] _parameters = Array.Empty<AudioParameter>();
    private float[] _values = Array.Empty<float>();
    private float[][] _inputPlanes = CreatePlanes();
    private float[][] _outputPlanes = CreatePlanes();
    private string _state = "Loading plugin";
    private string? _error;
    private string _pluginName = "VST3 plugin";
    private string _vendor = "";
    private string _version = "";
    private int _latency;
    private bool _hasEditor;
    private bool _isInstrument;
    private volatile bool _ready;
    private bool _disposed;
    private int _started;
    private double _tempo = 120.0;
    private bool _transportPlaying;
    private int _transportReset;
    private string? _lastStateBase64;

    public Vst3Processor(
        string identifier,
        string? stateBase64 = null,
        IReadOnlyDictionary<string, float>? pendingParameters = null,
        bool start = true,
        bool allowInstrument = false)
    {
        _identifier = identifier;
        _allowInstrument = allowInstrument;
        _lastStateBase64 = stateBase64;
        if (!string.IsNullOrWhiteSpace(stateBase64))
        {
            try { _initialState = Convert.FromBase64String(stateBase64); }
            catch { _initialState = null; }
        }

        _pendingParameters = pendingParameters;
        if (start)
        {
            Start();
        }
    }

    public event Action? Changed;

    public string TypeId => Id;
    public string DisplayName => _pluginName;
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _ready ? _latency : 0;
    public string Identifier => _identifier;
    public string PluginName => _pluginName;
    public string Vendor => _vendor;
    public string Version => _version;
    public string Status => _state;
    public string? Error => _error;
    public bool IsReady => _ready;
    public bool IsInstrument => _ready ? _isInstrument : _allowInstrument;
    public bool HasEditor => _hasEditor;
    public bool IsEditorOpen
    {
        get
        {
            try { return _wrapper?.InnerWrapper.IsEditorOpen == true; }
            catch { return false; }
        }
    }

    /// <summary>Starts loading on the thread pool; safe to call more than once.</summary>
    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(LoadAsync);
    }

    private async Task LoadAsync()
    {
        bool gateHeld = false;
        try
        {
            gateHeld = await Vst3ProcessorFactory.NativeGate
                .WaitAsync(TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            if (!gateHeld)
            {
                Fail("Another VST3 plugin is still loading or unloading. Try again.");
                return;
            }

            SetState("Loading plugin", null);
            RaiseChanged();

            var wrapper = new ThreadedVst3Wrapper();
            if (_disposed)
            {
                wrapper.Dispose();
                return;
            }

            _wrapper = wrapper;
            if (!await wrapper.LoadPluginAsync(_identifier))
            {
                Fail("The VST3 module could not be loaded.");
                return;
            }

            if (!await wrapper.InitializeAsync(48000, MaxBlockFrames))
            {
                Fail("The VST3 plugin could not be initialized at 48 kHz.");
                return;
            }

            if (_initialState is { Length: > 0 } && !await wrapper.SetStateAsync(_initialState))
            {
                AppLog.Warning($"VST3 '{_identifier}' did not accept its saved state.");
            }

            _pluginName = await wrapper.GetNameAsync();
            _vendor = await wrapper.GetVendorAsync();
            _version = await wrapper.GetVersionAsync() ?? "";
            _hasEditor = await wrapper.HasEditorAsync();
            bool isEffect = await wrapper.GetIsEffectAsync();
            bool isInstrument = await wrapper.GetIsInstrumentAsync();
            bool isMidiOnly = await wrapper.GetIsMidiOnlyAsync();
            if (isMidiOnly || (!isEffect && !(isInstrument && _allowInstrument)))
            {
                Fail(isMidiOnly
                    ? "This VST3 is a MIDI-only plugin and has no audio output."
                    : "This VST3 is an instrument. Add it as a VST3 instrument node.");
                return;
            }

            _isInstrument = isInstrument;

            var parameters = await wrapper.GetAllParametersAsync();
            BuildParameters(parameters);
            await ApplyPendingParametersAsync(wrapper);
            await wrapper.RunOnPluginThreadAsync(() => _latency = wrapper.InnerWrapper.LatencySamples);

            if (_disposed)
            {
                wrapper.Dispose();
                if (ReferenceEquals(_wrapper, wrapper)) _wrapper = null;
                return;
            }

            wrapper.SetTempo(_tempo);
            wrapper.SetTransportState(_transportPlaying);
            if (Volatile.Read(ref _transportReset) > 0)
            {
                wrapper.ResetTransportPosition();
            }

            _ready = true;
            SetState("Ready", null);
            RaiseChanged();
            AppLog.Info($"VST3 '{_pluginName}' ready ({_vendor}, latency {_latency} frames).");
        }
        catch (Exception ex)
        {
            Fail(ex.GetBaseException().Message);
            AppLog.Error($"Could not host VST3 '{_identifier}'.", ex);
        }
        finally
        {
            if (gateHeld)
            {
                Vst3ProcessorFactory.NativeGate.Release();
            }
        }
    }

    private void BuildParameters(List<VST3Parameter> parameters)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audioParameters = new AudioParameter[parameters.Count];
        var values = new float[parameters.Count];

        for (int i = 0; i < parameters.Count; i++)
        {
            var pluginParameter = parameters[i];
            string name = string.IsNullOrWhiteSpace(pluginParameter.Name)
                ? $"Parameter {i + 1}"
                : pluginParameter.Name;
            if (!names.Add(name))
            {
                name = $"{name} [{pluginParameter.Id}]";
                names.Add(name);
            }

            int index = i;
            values[i] = (float)pluginParameter.CurrentValue;
            audioParameters[i] = new AudioParameter
            {
                Name = name,
                Min = (float)pluginParameter.MinValue,
                Max = (float)pluginParameter.MaxValue,
                Default = (float)pluginParameter.DefaultValue,
                Get = () => _values.Length > index ? _values[index] : 0f,
                Set = value =>
                {
                    if (_values.Length > index)
                    {
                        _values[index] = value;
                    }

                    _wrapper?.SetParameter(pluginParameter.Id, value);
                },
            };
        }

        _values = values;
        _parameters = audioParameters;
    }

    private async Task ApplyPendingParametersAsync(ThreadedVst3Wrapper wrapper)
    {
        if (_pendingParameters == null || _pendingParameters.Count == 0 || _parameters.Length == 0)
        {
            return;
        }

        var pluginParameters = await wrapper.GetAllParametersAsync();
        for (int i = 0; i < _parameters.Length && i < pluginParameters.Count; i++)
        {
            if (!_pendingParameters.TryGetValue(_parameters[i].Name, out float value))
            {
                continue;
            }

            _values[i] = value;
            await wrapper.SetParameterAsync(pluginParameters[i].Id, value);
        }
    }

    public void EnqueueMidi(byte status, byte data1, byte data2, int sampleOffset = 0) =>
        _midi.TryWrite(new Vst3MidiEvent(status, data1, data2, sampleOffset));

    public void SetTempo(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm <= 0)
        {
            return;
        }

        _tempo = bpm;
        _wrapper?.SetTempo(bpm);
    }

    public void SetTransportState(bool playing)
    {
        _transportPlaying = playing;
        _wrapper?.SetTransportState(playing);
    }

    public void ResetTransportPosition()
    {
        Interlocked.Increment(ref _transportReset);
        _wrapper?.ResetTransportPosition();
    }

    private MidiEvent[] GetMidiBuffer(int count)
    {
        MidiEvent[]? buffer = _midiBuffers[count];
        if (buffer == null)
        {
            buffer = new MidiEvent[count];
            _midiBuffers[count] = buffer;
        }

        return buffer;
    }

    public void Process(Span<float> samples, int frames)
    {
        if (!_ready || _disposed || frames <= 0 || frames > MaxBlockFrames || samples.Length < frames * 2)
        {
            return;
        }

        // A VST3 node is shared by every route that uses it; the native wrapper
        // is not re-entrant, so serialise the rare fan-out case.
        lock (_audioLock)
        {
            if (!_ready || _disposed || _wrapper == null)
            {
                return;
            }

            EnsurePlanes(frames);
            for (int channel = 0; channel < 2; channel++)
            {
                var plane = _inputPlanes[channel];
                for (int frame = 0; frame < frames; frame++)
                {
                    plane[frame] = samples[frame * 2 + channel];
                }
            }

            int midiCount = _midi.Read(_midiDrain);
            if (midiCount > 0)
            {
                MidiEvent[] events = GetMidiBuffer(midiCount);
                for (int i = 0; i < midiCount; i++)
                {
                    Vst3MidiEvent midi = _midiDrain[i];
                    events[i].Status = midi.Status;
                    events[i].Data1 = midi.Data1;
                    events[i].Data2 = midi.Data2;
                    events[i].SampleOffset = Math.Clamp(midi.SampleOffset, 0, frames - 1);
                }

                _wrapper.ProcessMidi(events);
            }

            if (!_wrapper.ProcessAudio(_inputPlanes, _outputPlanes, 2, frames))
            {
                return;
            }

            for (int channel = 0; channel < 2; channel++)
            {
                var plane = _outputPlanes[channel];
                for (int frame = 0; frame < frames; frame++)
                {
                    samples[frame * 2 + channel] = plane[frame];
                }
            }
        }
    }

    private static float[][] CreatePlanes() =>
        new[] { new float[MaxBlockFrames], new float[MaxBlockFrames] };

    private void EnsurePlanes(int frames)
    {
        if (_inputPlanes.Length == 2 &&
            _outputPlanes.Length == 2 &&
            _inputPlanes[0].Length >= frames)
        {
            return;
        }

        _inputPlanes = new[] { new float[frames], new float[frames] };
        _outputPlanes = new[] { new float[frames], new float[frames] };
    }

    public void Reset()
    {
        // VST3 has no generic reset command. Re-applying the state would reset
        // more than the engine wants; leave the plugin running.
    }

    public string? CaptureStateBase64()
    {
        var wrapper = _wrapper;
        if (wrapper == null || !_ready)
        {
            return _lastStateBase64;
        }

        try
        {
            var task = wrapper.GetStateAsync();
            if (!task.Wait(2000))
            {
                return _lastStateBase64;
            }

            byte[]? data = task.Result;
            if (data is { Length: > 0 })
            {
                _lastStateBase64 = Convert.ToBase64String(data);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not read VST3 state for '{_identifier}': {ex.Message}");
        }

        return _lastStateBase64;
    }

    public bool ApplyStateBase64(string stateBase64)
    {
        var wrapper = _wrapper;
        if (wrapper == null || !_ready || string.IsNullOrWhiteSpace(stateBase64))
        {
            return false;
        }

        try
        {
            byte[] state = Convert.FromBase64String(stateBase64);
            var task = Task.Run(() => wrapper.SetStateAsync(state));
            if (!task.Wait(2000) || !task.Result)
            {
                return false;
            }

            _lastStateBase64 = stateBase64;
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not apply VST3 state for '{_identifier}': {ex.Message}");
            return false;
        }
    }

    public bool OpenEditor(string title)
    {
        if (!_ready || !_hasEditor || _wrapper == null)
        {
            return false;
        }

        if (_editor != null && !IsEditorOpen)
        {
            _editor.Dispose();
            _editor = null;
        }

        _editor ??= new VstEditorController(_wrapper);
        _editor.OpenEditor(string.IsNullOrWhiteSpace(title) ? _pluginName : title);
        bool open = _editor.IsOpen;
        if (open)
        {
            StartParameterPolling();
        }

        return open;
    }

    public void CloseEditor()
    {
        StopParameterPolling();
        _editor?.CloseEditor();
        _editor?.Dispose();
        _editor = null;
        RaiseChanged();
    }

    /// <summary>
    /// Mirrors values changed inside the plugin's own editor back into the
    /// cached parameter list. The VST3 host does not notify the wrapper when a
    /// parameter moves, so poll while the editor is open and let Changed()
    /// republish the graph.
    /// </summary>
    private void StartParameterPolling()
    {
        var wrapper = _wrapper;
        if (wrapper == null)
        {
            return;
        }

        CancellationTokenSource cancellation;
        lock (_pollLock)
        {
            try { _pollCancellation?.Cancel(); } catch { }
            cancellation = new CancellationTokenSource();
            _pollCancellation = cancellation;
        }

        _ = Task.Run(() => PollParametersAsync(wrapper, cancellation));
    }

    private void StopParameterPolling()
    {
        CancellationTokenSource? cancellation;
        lock (_pollLock)
        {
            cancellation = _pollCancellation;
            _pollCancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
        }
    }

    private async Task PollParametersAsync(
        ThreadedVst3Wrapper wrapper,
        CancellationTokenSource cancellation)
    {
        bool raisedForClose = false;
        int tick = 0;
        try
        {
            while (!_disposed && !cancellation.IsCancellationRequested)
            {
                await Task.Delay(200, cancellation.Token);
                bool open;
                try
                {
                    open = _editor?.IsOpen == true && IsEditorOpen;
                }
                catch
                {
                    open = false;
                }

                if (!open)
                {
                    raisedForClose = true;
                    break;
                }

                try
                {
                    var pluginParameters = await wrapper.GetAllParametersAsync();
                    bool changed = false;
                    int count = Math.Min(_values.Length, pluginParameters.Count);
                    for (int i = 0; i < count; i++)
                    {
                        float value = (float)pluginParameters[i].CurrentValue;
                        if (!float.IsFinite(value) ||
                            Math.Abs(value - _values[i]) <= 1e-5f)
                        {
                            continue;
                        }

                        _values[i] = value;
                        changed = true;
                    }

                    if (changed)
                    {
                        RaiseChanged();
                    }

                    // Cache editor-only state roughly every 2 s so a watchdog
                    // kill does not discard settings that only live in the plugin.
                    if (++tick % 10 == 0)
                    {
                        try
                        {
                            var stateTask = wrapper.GetStateAsync();
                            if (stateTask.Wait(2000) && stateTask.Result is { Length: > 0 } data)
                            {
                                _lastStateBase64 = Convert.ToBase64String(data);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Warning($"VST3 parameter polling for '{_identifier}' hit an error: {ex.Message}");
                    await Task.Delay(500, cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning($"VST3 parameter polling for '{_identifier}' stopped: {ex.Message}");
        }
        finally
        {
            lock (_pollLock)
            {
                if (ReferenceEquals(_pollCancellation, cancellation))
                {
                    _pollCancellation = null;
                }
            }

            cancellation.Dispose();
            if (raisedForClose && !_disposed)
            {
                RaiseChanged();
            }
        }
    }

    private void SetState(string status, string? error)
    {
        lock (_stateLock)
        {
            _state = status;
            _error = error;
        }
    }

    private void Fail(string message)
    {
        if (_disposed)
        {
            return;
        }

        _ready = false;
        SetState("Error", message);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready = false;
        if (!Vst3ProcessorFactory.NativeGate.Wait(TimeSpan.FromSeconds(30)))
        {
            AppLog.Warning(
                $"Gave up waiting to unload VST3 '{_identifier}'; another plugin is still loading or unloading.");
            return;
        }

        try
        {
            CloseEditor();
            _wrapper?.Dispose();
            _wrapper = null;
        }
        finally
        {
            Vst3ProcessorFactory.NativeGate.Release();
        }
    }
}
