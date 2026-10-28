using OwnVST3Host;
using OwnVST3Host.NativeWindow;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Hosts one VST3 effect. The native wrapper owns the plugin thread; this class
/// adapts the plugin to the engine's stereo interleaved processing contract.
/// </summary>
public sealed class Vst3Processor : IAudioProcessor, IParameterizedProcessor, ISharedProcessor, IDisposable
{
    public const string Id = "vst3";
    private const int MaxBlockFrames = 1024;

    private readonly string _identifier;
    private readonly byte[]? _initialState;
    private readonly IReadOnlyDictionary<string, float>? _pendingParameters;
    private readonly object _audioLock = new();
    private readonly object _stateLock = new();

    private ThreadedVst3Wrapper? _wrapper;
    private VstEditorController? _editor;
    private AudioParameter[] _parameters = Array.Empty<AudioParameter>();
    private float[] _values = Array.Empty<float>();
    private float[][] _inputPlanes = Array.Empty<float[]>();
    private float[][] _outputPlanes = Array.Empty<float[]>();
    private string _state = "Loading plugin";
    private string? _error;
    private string _pluginName = "VST3 plugin";
    private string _vendor = "";
    private string _version = "";
    private int _latency;
    private bool _hasEditor;
    private bool _ready;
    private bool _disposed;
    private int _started;
    private string? _lastStateBase64;

    public Vst3Processor(
        string identifier,
        string? stateBase64 = null,
        IReadOnlyDictionary<string, float>? pendingParameters = null,
        bool start = true)
    {
        _identifier = identifier;
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

    public event Action<Vst3Processor>? Changed;

    public string TypeId => Id;
    public string DisplayName => _pluginName;
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _latency;
    public string Identifier => _identifier;
    public string PluginName => _pluginName;
    public string Vendor => _vendor;
    public string Version => _version;
    public string Status => _state;
    public string? Error => _error;
    public bool IsReady => _ready;
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
        try
        {
            SetState("Loading plugin", null);
            RaiseChanged();

            var wrapper = new ThreadedVst3Wrapper();
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
            bool isMidiOnly = await wrapper.GetIsMidiOnlyAsync();
            if (!isEffect || isMidiOnly)
            {
                Fail(isMidiOnly
                    ? "This VST3 is a MIDI-only plugin, not an audio effect."
                    : "This VST3 is an instrument, not an audio effect.");
                return;
            }

            var parameters = await wrapper.GetAllParametersAsync();
            BuildParameters(parameters);
            await ApplyPendingParametersAsync(wrapper);
            await wrapper.RunOnPluginThreadAsync(() => _latency = wrapper.InnerWrapper.LatencySamples);

            if (_disposed)
            {
                return;
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
        return _editor.IsOpen;
    }

    public void CloseEditor()
    {
        _editor?.CloseEditor();
        _editor?.Dispose();
        _editor = null;
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
            Changed?.Invoke(this);
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
        CloseEditor();
        _wrapper?.Dispose();
        _wrapper = null;
    }
}
