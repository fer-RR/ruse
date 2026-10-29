using System.Diagnostics;
using System.IO.Pipes;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Audio-thread proxy for a VST3 plugin hosted in a child process. A crashing
/// plugin takes down only that process; this node reports the failure and
/// passes the dry signal through.
/// </summary>
public sealed class IsolatedVst3Processor : IVst3Processor
{
    private const int MaxFrames = 2048;
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly string _identifier;
    private readonly string? _initialState;
    private readonly IReadOnlyDictionary<string, float>? _pendingParameters;
    private readonly object _audioLock = new();
    private readonly object _controlLock = new();
    private readonly object _stateLock = new();

    private NamedPipeServerStream? _audioPipe;
    private NamedPipeServerStream? _controlPipe;
    private Process? _process;
    private volatile Vst3HostInfo? _info;
    private volatile AudioParameter[] _parameters = Array.Empty<AudioParameter>();
    private volatile string? _error;
    private volatile bool _ready;
    private int _started;
    private bool _disposed;

    public IsolatedVst3Processor(
        string identifier,
        string? stateBase64 = null,
        IReadOnlyDictionary<string, float>? pendingParameters = null,
        bool start = true)
    {
        _identifier = identifier;
        _initialState = stateBase64;
        _pendingParameters = pendingParameters;
        if (start)
        {
            Start();
        }
    }

    public event Action? Changed;

    public string TypeId => Vst3Processor.Id;
    public string Identifier => _identifier;
    public string DisplayName => _info?.Name ?? "VST3 plugin";
    public string PluginName => _info?.Name ?? "VST3 plugin";
    public string Vendor => _info?.Vendor ?? "";
    public string Version => _info?.Version ?? "";
    public string Status => _info?.Status ?? (_error == null ? "Loading plugin" : "Error");
    public string? Error => _error ?? _info?.Error;
    public bool IsReady => _ready && _info?.IsReady == true;
    public bool HasEditor => _info?.HasEditor == true;
    public bool IsEditorOpen => _info?.IsEditorOpen == true;
    public int LatencySamples => _info?.LatencySamples ?? 0;
    public IReadOnlyList<AudioParameter> Parameters => _parameters;

    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(ConnectAsync);
    }

    private async Task ConnectAsync()
    {
        try
        {
            string audioName = "ruse-vst3-a-" + Guid.NewGuid().ToString("N");
            string controlName = "ruse-vst3-c-" + Guid.NewGuid().ToString("N");
            _audioPipe = new NamedPipeServerStream(audioName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _controlPipe = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                Fail("Could not locate the Ruse executable for the VST3 host.");
                return;
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add("--vst3-host");
            startInfo.ArgumentList.Add(audioName);
            startInfo.ArgumentList.Add(controlName);
            startInfo.ArgumentList.Add(_identifier);
            startInfo.ArgumentList.Add(_initialState ?? "");

            _process = System.Diagnostics.Process.Start(startInfo);
            if (_process == null)
            {
                Fail("The isolated VST3 host could not start.");
                return;
            }

            Task audioConnect = _audioPipe.WaitForConnectionAsync();
            Task controlConnect = _controlPipe.WaitForConnectionAsync();
            Task connectTask = Task.WhenAll(audioConnect, controlConnect);
            if (await Task.WhenAny(connectTask, Task.Delay(15000)) != connectTask)
            {
                Fail("The isolated VST3 host did not connect.");
                return;
            }

            _info = await WaitForInfoAsync(TimeSpan.FromSeconds(60));
            if (_info == null)
            {
                Fail("The isolated VST3 host did not report ready.");
                return;
            }

            BuildParameters(_info);
            ApplyPendingParameters();

            _ready = true;
            RaiseChanged();
            AppLog.Info($"Isolated VST3 '{DisplayName}' ready.");
        }
        catch (Exception ex)
        {
            Fail("The isolated VST3 host failed: " + ex.GetBaseException().Message, ex);
        }
    }

    private async Task<Vst3HostInfo?> WaitForInfoAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            var response = SendControl(new Vst3HostRequest { Command = "info" });
            if (response?.Info != null)
            {
                _info = response.Info;
                if (response.Info.Error != null)
                {
                    _error = response.Info.Error;
                }

                RaiseChanged();
                if (response.Info.IsReady || response.Info.Error != null)
                {
                    return response.Info;
                }
            }

            await Task.Delay(250);
        }

        return null;
    }

    private void BuildParameters(Vst3HostInfo info)
    {
        var parameters = new List<AudioParameter>();
        foreach (var source in info.Parameters)
        {
            var parameter = source;
            parameters.Add(new AudioParameter
            {
                Name = parameter.Name,
                Min = parameter.Min,
                Max = parameter.Max,
                Default = parameter.Default,
                Unit = parameter.Unit,
                IsToggle = parameter.IsToggle,
                IsChoice = parameter.IsChoice,
                Options = parameter.Options,
                Get = () => parameter.Value,
                Set = value =>
                {
                    parameter.Value = value;
                    SendControl(new Vst3HostRequest
                    {
                        Command = "set",
                        Name = parameter.Name,
                        Value = value,
                    });
                },
            });
        }

        _parameters = parameters.ToArray();
    }

    private void ApplyPendingParameters()
    {
        if (_pendingParameters == null)
        {
            return;
        }

        foreach (var (name, value) in _pendingParameters)
        {
            SendControl(new Vst3HostRequest { Command = "set", Name = name, Value = value });
        }
    }

    private Vst3HostResponse? SendControl(Vst3HostRequest request)
    {
        lock (_controlLock)
        {
            if (_controlPipe == null || !_controlPipe.IsConnected)
            {
                return null;
            }

            try
            {
                Vst3HostProtocol.WriteControl(_controlPipe, request);
                return Vst3HostProtocol.ReadControl(_controlPipe);
            }
            catch (Exception ex)
            {
                Fail("The isolated VST3 host lost its control connection.", ex);
                return null;
            }
        }
    }

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || frames > MaxFrames || samples.Length < frames * Channels)
        {
            return;
        }

        if (!IsReady)
        {
            return;
        }

        lock (_audioLock)
        {
            var pipe = _audioPipe;
            if (pipe == null || !pipe.IsConnected || _disposed)
            {
                return;
            }

            try
            {
                Vst3HostProtocol.WriteAudio(
                    pipe, samples[..(frames * Channels)], frames, Channels,
                    Vst3HostProtocol.CommandProcess);
                int read = Vst3HostProtocol.ReadAudio(pipe, samples, out int outFrames, out int outChannels, out _)
                    ? outFrames * outChannels
                    : 0;
                if (read <= 0)
                {
                    Fail("The isolated VST3 host returned an empty block.");
                }
            }
            catch (Exception ex)
            {
                Fail("The isolated VST3 host stopped processing audio.", ex);
            }
        }
    }

    public void Reset()
    {
    }

    public string? CaptureStateBase64()
    {
        var response = SendControl(new Vst3HostRequest { Command = "state" });
        return response is { Ok: true } ? response.State : null;
    }

    public bool OpenEditor(string title)
    {
        var response = SendControl(new Vst3HostRequest { Command = "open-editor" });
        bool opened = response is { Ok: true };
        if (_info != null)
        {
            _info.IsEditorOpen = opened;
        }

        RaiseChanged();
        return opened;
    }

    public void CloseEditor()
    {
        SendControl(new Vst3HostRequest { Command = "close-editor" });
        if (_info != null)
        {
            _info.IsEditorOpen = false;
        }

        RaiseChanged();
    }

    private void Fail(string message, Exception? exception = null)
    {
        _ready = false;
        _error = message;
        if (exception != null)
        {
            AppLog.Error(message, exception);
        }
        else
        {
            AppLog.Warning(message);
        }

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
        try
        {
            if (_controlPipe is { IsConnected: true })
            {
                Vst3HostProtocol.WriteControl(_controlPipe, new Vst3HostRequest { Command = "stop" });
            }
        }
        catch
        {
        }

        try
        {
            if (_audioPipe is { IsConnected: true })
            {
                Vst3HostProtocol.WriteAudio(_audioPipe, Span<float>.Empty, 0, Channels,
                    Vst3HostProtocol.CommandStop);
            }
        }
        catch
        {
        }

        _audioPipe?.Dispose();
        _controlPipe?.Dispose();
        _audioPipe = null;
        _controlPipe = null;

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;
    }
}
