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
    private string? _lastState;
    private readonly object _audioLock = new();
    private readonly object _controlLock = new();
    private readonly object _stateLock = new();
    private readonly object _editorPollLock = new();
    private readonly float[] _audioInput = new float[MaxFrames * Channels];
    private const int AudioSlotCount = 4;
    private readonly AudioSlot[] _audioSlots = CreateAudioSlots();
    private int _freeAudioSlots = (1 << AudioSlotCount) - 1;
    private AudioSlot? _audioPending;
    private AudioSlot? _audioReady;
    private long _audioEpoch;
    private Thread? _audioThread;
    private volatile bool _audioStop;

    private NamedPipeServerStream? _audioPipe;
    private NamedPipeServerStream? _controlPipe;
    private Vst3SharedAudio? _sharedAudio;
    private Process? _process;
    private volatile Vst3HostInfo? _info;
    private volatile AudioParameter[] _parameters = Array.Empty<AudioParameter>();
    private volatile string? _error;
    private volatile bool _ready;
    private int _started;
    private long _audioCallStarted;
    private long _controlCallStarted;
    private Thread? _watchdog;
    private int _hostKilled;
    private int _restartPending;
    private long _restartAttempts;
    private IReadOnlyDictionary<string, float>? _restartParameters;
    private volatile CancellationTokenSource? _editorPoll;
    private bool _disposed;

    public IsolatedVst3Processor(
        string identifier,
        string? stateBase64 = null,
        IReadOnlyDictionary<string, float>? pendingParameters = null,
        bool start = true)
    {
        _identifier = identifier;
        _initialState = stateBase64;
        _lastState = stateBase64;
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
    public int LatencySamples => IsReady ? (_info?.LatencySamples ?? 0) + AudioEngineConfig.BlockFrames : 0;
    public IReadOnlyList<AudioParameter> Parameters => _parameters;

    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _watchdog = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "Vst3HostWatchdog",
        };
        _watchdog.Start();
        _audioThread = new Thread(AudioWorkerLoop)
        {
            IsBackground = true,
            Name = "Vst3AudioWorker",
        };
        _audioThread.Start();
        _ = Task.Run(ConnectAsync);
    }

    private async Task ConnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_audioLock)
            {
                ResetHost();
            }

            string audioName = "ruse-vst3-a-" + Guid.NewGuid().ToString("N");
            string controlName = "ruse-vst3-c-" + Guid.NewGuid().ToString("N");
            _audioPipe = new NamedPipeServerStream(audioName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _controlPipe = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _sharedAudio = Vst3SharedAudio.Create(audioName);

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
            string handshakeToken = Guid.NewGuid().ToString("N");
            startInfo.ArgumentList.Add(handshakeToken);

            _process = System.Diagnostics.Process.Start(startInfo);
            if (_process == null)
            {
                Fail("The isolated VST3 host could not start.");
                ScheduleRestart();
                return;
            }

            if (_disposed)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
                return;
            }

            _process.EnableRaisingEvents = true;
            _process.Exited += OnHostExited;

            Task audioConnect = _audioPipe.WaitForConnectionAsync();
            Task controlConnect = _controlPipe.WaitForConnectionAsync();
            Task connectTask = Task.WhenAll(audioConnect, controlConnect);
            if (await Task.WhenAny(connectTask, Task.Delay(15000)) != connectTask)
            {
                Fail("The isolated VST3 host did not connect.");
                ScheduleRestart();
                return;
            }

            var handshake = SendControl(new Vst3HostRequest
            {
                Command = "hello",
                Name = handshakeToken,
            });
            if (handshake is not { Ok: true })
            {
                Fail("The isolated VST3 host rejected the handshake.");
                ScheduleRestart();
                return;
            }

            _info = await WaitForInfoAsync(TimeSpan.FromSeconds(60));
            if (_info == null)
            {
                Fail("The isolated VST3 host did not report ready.");
                ScheduleRestart();
                return;
            }

            if (_info.Error != null)
            {
                Fail(_info.Error);
                return;
            }

            BuildParameters(_info);
            ApplyPendingParameters();

            _ready = true;
            _error = null;
            _restartParameters = null;
            Interlocked.Exchange(ref _restartAttempts, 0);
            RaiseChanged();
            AppLog.Info($"Isolated VST3 '{DisplayName}' ready.");
        }
        catch (Exception ex)
        {
            Fail("The isolated VST3 host failed: " + ex.GetBaseException().Message, ex);
            ScheduleRestart();
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
        var pending = _restartParameters ?? _pendingParameters;
        if (pending == null)
        {
            return;
        }

        foreach (var (name, value) in pending)
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

            Interlocked.Exchange(ref _controlCallStarted, Environment.TickCount64);
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
            finally
            {
                Interlocked.Exchange(ref _controlCallStarted, 0);
            }
        }
    }

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || frames > MaxFrames || samples.Length < frames * Channels)
        {
            return;
        }

        if (!IsReady || _disposed)
        {
            return;
        }

        int validSamples = frames * Channels;
        long epoch = Volatile.Read(ref _audioEpoch);

        // Stage the dry input before consuming the previous block so a fast
        // worker can never make this block's output appear early.
        samples[..validSamples].CopyTo(_audioInput);

        AudioSlot? ready = Interlocked.Exchange(ref _audioReady, null);
        if (ready != null)
        {
            if (ready.Epoch == epoch)
            {
                int readyFrames = Math.Min(frames, ready.Frames);
                ready.Samples.AsSpan(0, readyFrames * Channels).CopyTo(samples);
                if (readyFrames < frames)
                {
                    samples[(readyFrames * Channels)..validSamples].Clear();
                }
            }

            ReturnAudioSlot(ready);
        }

        AudioSlot? pending = RentAudioSlot();
        if (pending == null)
        {
            return;
        }

        _audioInput.AsSpan(0, validSamples).CopyTo(pending.Samples);
        pending.Frames = frames;
        pending.Epoch = epoch;

        AudioSlot? dropped = Interlocked.Exchange(ref _audioPending, pending);
        if (dropped != null)
        {
            ReturnAudioSlot(dropped);
        }

    }

    private void AudioWorkerLoop()
    {
        while (!_audioStop)
        {
            AudioSlot? pending = Interlocked.Exchange(ref _audioPending, null);
            if (pending == null)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = pending.Frames;
            long epoch = pending.Epoch;
            bool processed = HostProcess(pending.Samples, frames);

            if (processed && !_audioStop && epoch == Volatile.Read(ref _audioEpoch))
            {
                pending.Frames = frames;
                pending.Epoch = epoch;
                AudioSlot? dropped = Interlocked.Exchange(ref _audioReady, pending);
                if (dropped != null)
                {
                    ReturnAudioSlot(dropped);
                }
            }
            else
            {
                ReturnAudioSlot(pending);
            }
        }
    }

    private static AudioSlot[] CreateAudioSlots()
    {
        var slots = new AudioSlot[AudioSlotCount];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new AudioSlot(i);
        }

        return slots;
    }

    private AudioSlot? RentAudioSlot()
    {
        while (true)
        {
            int mask = Volatile.Read(ref _freeAudioSlots);
            if (mask == 0)
            {
                return null;
            }

            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            int next = mask & ~(1 << index);
            if (Interlocked.CompareExchange(ref _freeAudioSlots, next, mask) == mask)
            {
                return _audioSlots[index];
            }
        }
    }

    private void ReturnAudioSlot(AudioSlot slot) =>
        Interlocked.Or(ref _freeAudioSlots, 1 << slot.Index);


    private sealed class AudioSlot
    {
        public AudioSlot(int index) => Index = index;

        public int Index { get; }
        public float[] Samples { get; } = new float[MaxFrames * Channels];
        public int Frames;
        public long Epoch;
    }

    private bool HostProcess(float[] samples, int frames)
    {
        lock (_audioLock)
        {
            var shared = _sharedAudio;
            if (shared == null || _disposed || !IsReady)
            {
                return false;
            }

            try
            {
                Interlocked.Exchange(ref _audioCallStarted, Environment.TickCount64);
                if (!shared.Process(samples, frames))
                {
                    Fail("The isolated VST3 host did not return an audio block.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Fail("The isolated VST3 host stopped processing audio.", ex);
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _audioCallStarted, 0);
            }
        }
    }

    public void Reset()
    {
    }

    public string? CaptureStateBase64()
    {
        var response = SendControl(new Vst3HostRequest { Command = "state" });
        if (response is { Ok: true, State.Length: > 0 })
        {
            lock (_stateLock)
            {
                _lastState = response.State;
            }

            return response.State;
        }

        lock (_stateLock)
        {
            return _lastState;
        }
    }

    public bool OpenEditor(string title)
    {
        var response = SendControl(new Vst3HostRequest { Command = "open-editor" });
        bool opened = response is { Ok: true };
        if (_info != null)
        {
            _info.IsEditorOpen = opened;
        }

        if (opened)
        {
            StartEditorPolling();
        }

        RaiseChanged();
        return opened;
    }

    public void CloseEditor()
    {
        StopEditorPolling();
        SendControl(new Vst3HostRequest { Command = "close-editor" });
        if (_info != null)
        {
            _info.IsEditorOpen = false;
        }

        RaiseChanged();
    }

    /// <summary>
    /// The child host cannot notify us when a parameter is changed in its native
    /// editor window. Poll the existing info command while the editor is open
    /// and merge values into the stable parameter objects the UI already holds.
    /// </summary>
    private void StartEditorPolling()
    {
        CancellationTokenSource cancellation;
        lock (_editorPollLock)
        {
            try { _editorPoll?.Cancel(); } catch { }
            cancellation = new CancellationTokenSource();
            _editorPoll = cancellation;
        }

        _ = Task.Run(() => PollEditorParametersAsync(cancellation));
    }

    private void StopEditorPolling()
    {
        CancellationTokenSource? cancellation;
        lock (_editorPollLock)
        {
            cancellation = _editorPoll;
            _editorPoll = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
        }
    }

    private async Task PollEditorParametersAsync(CancellationTokenSource cancellation)
    {
        int ticks = 0;
        try
        {
            while (!_disposed && !cancellation.IsCancellationRequested)
            {
                await Task.Delay(250, cancellation.Token);
                if (!IsEditorOpen)
                {
                    break;
                }

                try
                {
                    // Keep a recent editor-only state snapshot so a watchdog kill
                    // does not discard settings that only exist inside the plugin.
                    if (++ticks % 8 == 0)
                    {
                        var state = SendControl(new Vst3HostRequest { Command = "state" });
                        if (state is { Ok: true, State.Length: > 0 })
                        {
                            lock (_stateLock)
                            {
                                _lastState = state.State;
                            }
                        }
                    }

                    var response = SendControl(new Vst3HostRequest { Command = "info" });
                    if (response?.Info == null)
                    {
                        await Task.Delay(250, cancellation.Token);
                        continue;
                    }

                    if (MergeEditorInfo(response.Info))
                    {
                        RaiseChanged();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Warning($"Isolated VST3 editor polling for '{_identifier}' hit an error: {ex.Message}");
                    await Task.Delay(500, cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Isolated VST3 editor polling for '{_identifier}' stopped: {ex.Message}");
        }
        finally
        {
            lock (_editorPollLock)
            {
                if (ReferenceEquals(_editorPoll, cancellation))
                {
                    _editorPoll = null;
                }
            }

            cancellation.Dispose();
            if (!_disposed)
            {
                RaiseChanged();
            }
        }
    }

    private bool MergeEditorInfo(Vst3HostInfo incoming)
    {
        var current = _info;
        if (current == null)
        {
            _info = incoming;
            return true;
        }

        bool changed = false;
        if (current.Status != incoming.Status)
        {
            current.Status = incoming.Status;
            changed = true;
        }

        if (current.IsEditorOpen != incoming.IsEditorOpen)
        {
            current.IsEditorOpen = incoming.IsEditorOpen;
            changed = true;
        }

        if (current.IsReady != incoming.IsReady)
        {
            current.IsReady = incoming.IsReady;
            changed = true;
        }

        if (current.LatencySamples != incoming.LatencySamples)
        {
            current.LatencySamples = incoming.LatencySamples;
            changed = true;
        }

        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var parameter in incoming.Parameters)
        {
            values[parameter.Name] = parameter.Value;
        }

        foreach (var parameter in current.Parameters)
        {
            if (!values.TryGetValue(parameter.Name, out float value) ||
                !float.IsFinite(value) ||
                Math.Abs(parameter.Value - value) <= 1e-5f)
            {
                continue;
            }

            parameter.Value = value;
            changed = true;
        }

        return changed;
    }

    private void Fail(string message, Exception? exception = null)
    {
        StopEditorPolling();
        _ready = false;
        _error = message;
        ClearAudioBuffers();
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

    private void ClearAudioBuffers()
    {
        AudioSlot? pending = Interlocked.Exchange(ref _audioPending, null);
        if (pending != null)
        {
            ReturnAudioSlot(pending);
        }

        AudioSlot? ready = Interlocked.Exchange(ref _audioReady, null);
        if (ready != null)
        {
            ReturnAudioSlot(ready);
        }
    }

    private void ResetHost()
    {
        Interlocked.Increment(ref _audioEpoch);
        ClearAudioBuffers();
        try
        {
            _audioPipe?.Dispose();
            _controlPipe?.Dispose();
        }
        catch
        {
        }

        _audioPipe = null;
        _controlPipe = null;
        _sharedAudio?.Dispose();
        _sharedAudio = null;

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Exited -= OnHostExited;
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;

        Interlocked.Exchange(ref _hostKilled, 0);
        Interlocked.Exchange(ref _audioCallStarted, 0);
        Interlocked.Exchange(ref _controlCallStarted, 0);
        _ready = false;
    }

    private void OnHostExited(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ready = false;
        _restartParameters ??= SnapshotParameters();
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        if (_disposed || Interlocked.Exchange(ref _restartPending, 1) != 0)
        {
            return;
        }

        long attempt = Interlocked.Increment(ref _restartAttempts);
        if (attempt > 3)
        {
            Interlocked.Exchange(ref _restartPending, 0);
            Fail("The VST3 host failed repeatedly; auto-restart gave up.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
                Interlocked.Exchange(ref _restartPending, 0);
                if (!_disposed)
                {
                    await ConnectAsync();
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _restartPending, 0);
                if (!_disposed)
                {
                    Fail("The VST3 host could not be restarted: " + ex.GetBaseException().Message, ex);
                    ScheduleRestart();
                }
            }
        });
    }

    private IReadOnlyDictionary<string, float>? SnapshotParameters()
    {
        try
        {
            return _parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Get());
        }
        catch
        {
            return null;
        }
    }

    private void WatchdogLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(20);
            if (_disposed)
            {
                return;
            }

            long now = Environment.TickCount64;
            long audio = Interlocked.Read(ref _audioCallStarted);
            if (audio != 0 && now - audio > 150)
            {
                KillHost("audio call exceeded 150 ms");
                continue;
            }

            long control = Interlocked.Read(ref _controlCallStarted);
            if (control != 0 && now - control > 10000)
            {
                KillHost("control call exceeded 10 s");
                continue;
            }
        }
    }

    private void KillHost(string reason)
    {
        if (Interlocked.Exchange(ref _hostKilled, 1) != 0)
        {
            return;
        }

        _ready = false;
        _restartParameters = SnapshotParameters();
        Fail($"The VST3 host stopped responding ({reason}); the node is bypassed until it recovers.");
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            _audioPipe?.Dispose();
            _controlPipe?.Dispose();
        }
        catch
        {

        }

        _sharedAudio?.Dispose();
        _sharedAudio = null;

        ScheduleRestart();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopEditorPolling();
        _audioStop = true;
        _watchdog?.Join(100);

        // Never write a stop command into a host that is already stuck; the
        // write itself could block forever. Kill it first in that case.
        bool stuck = _hostKilled != 0 ||
            Interlocked.Read(ref _audioCallStarted) != 0 ||
            Interlocked.Read(ref _controlCallStarted) != 0;
        if (!stuck)
        {
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
        }

        _audioPipe?.Dispose();
        _controlPipe?.Dispose();
        _audioPipe = null;
        _controlPipe = null;
        _sharedAudio?.Dispose();
        _sharedAudio = null;
        try
        {
            _audioThread?.Join(250);
        }
        catch
        {
        }

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
