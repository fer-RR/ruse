using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private sealed class MidiBindingEntry
    {
        public string NodeId { get; init; } = "";
        public string Target { get; init; } = "parameter";
        public int ProcessorIndex { get; init; }
        public int ParameterIndex { get; init; }
        public int Channel { get; init; }
        public MidiInputKind Kind { get; init; }
        public int Number { get; init; }
        public float Min { get; init; }
        public float Max { get; init; } = 1f;

        public string Label => $"CH{Channel + 1} {(Kind == MidiInputKind.Note ? "NOTE" : "CC")} {Number}";
    }

    private sealed record MidiLearnTarget(
        string NodeId,
        int ProcessorIndex,
        int ParameterIndex,
        string Target = "parameter");

    private const int MidiClockWindow = 25;

    private sealed record Vst3MidiTarget(
        IVst3Processor Processor,
        int Channel,
        bool TransportEnabled);

    private sealed record MidiBindingSnapshot(
        string Target,
        int ProcessorIndex,
        int ParameterIndex,
        int Channel,
        MidiInputKind Kind,
        int Number,
        float Min,
        float Max);

    private readonly MidiInputManager _midi = new();
    private readonly List<MidiBindingEntry> _midiBindings = new();
    private readonly ConcurrentQueue<MidiInputEvent> _midiUiQueue = new();
    private int _midiUiScheduled;
    private bool _midiDeviceWasAvailable = true;
    private string _midiDeviceSignature = "";
    private int _midiMonitorBusy;
    private int _midiOpenFailures;
    private long _midiOpenRetryAtTicks;
    private readonly long[] _midiClockTicks = new long[MidiClockWindow];
    private Vst3MidiTarget[] _vst3MidiTargets = Array.Empty<Vst3MidiTarget>();
    private int _midiClockIndex;
    private int _midiClockCount;
    private long _midiLastClockTimestamp;
    private long _midiLastTempoTimestamp;
    private MidiLearnTarget? _midiLearnTarget;
    private readonly HashSet<(string NodeId, int ProcessorIndex, int ParameterIndex)> _midiParameterChanges = new();
    private readonly object _midiParameterLock = new();

    private void InitializeMidi()
    {
        _midi.MessageReceived += OnMidiMessageReceived;
        _midi.ClockReceived += OnMidiClockReceived;
        _midi.TransportReceived += OnMidiTransportReceived;
        _ = ConfigureMidiAsync();
    }

    private async Task ConfigureMidiAsync()
    {
        PanicMidiTargets();
        _midiDeviceWasAvailable = true;
        _midiOpenFailures = 0;
        _midiOpenRetryAtTicks = 0;
        if (!_settings.MidiEnabled)
        {
            await _midi.CloseAsync().ConfigureAwait(true);
            return;
        }

        await Task.Run(_midi.RefreshDevices).ConfigureAwait(true);
        if (_disposed || !_settings.MidiEnabled)
        {
            await _midi.CloseAsync().ConfigureAwait(true);
            return;
        }

        _midiDeviceSignature = MidiDeviceSignature();
        if (string.IsNullOrWhiteSpace(_settings.MidiInputDevice))
        {
            _settings.MidiInputDevice = _midi.Devices
                .FirstOrDefault(device => !string.Equals(
                    device.Name, MidiInputManager.TestDeviceName, StringComparison.Ordinal))
                .Name ?? "";
            _settings.Save();
        }

        await TryOpenMidiAsync(_settings.MidiInputDevice).ConfigureAwait(true);
        PublishSettings();
    }

    private async Task TryOpenMidiAsync(string deviceName)
    {
        try
        {
            await _midi.OpenAsync(deviceName).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not open the MIDI input: " + ex.Message);
        }
    }

    private void MonitorMidiDevice()
    {
        if (_disposed ||
            !_settings.MidiEnabled ||
            string.IsNullOrWhiteSpace(_settings.MidiInputDevice) ||
            Interlocked.Exchange(ref _midiMonitorBusy, 1) != 0)
        {
            return;
        }

        string deviceName = _settings.MidiInputDevice;
        _ = Task.Run(() =>
        {
            try
            {
                _midi.RefreshDevices();
                string signature = string.Join("|", _midi.Devices.Select(device => device.Name));
                bool available = _midi.Devices.Any(device =>
                    string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase));
                try
                {
                    _ = _window.Dispatcher.BeginInvoke(new Action(() =>
                        ApplyMidiDeviceMonitor(deviceName, signature, available)));
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("MIDI device monitor failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _midiMonitorBusy, 0);
            }
        });
    }

    private void ApplyMidiDeviceMonitor(string deviceName, string signature, bool available)
    {
        if (_disposed ||
            !_settings.MidiEnabled ||
            !string.Equals(_settings.MidiInputDevice, deviceName, StringComparison.Ordinal))
        {
            return;
        }

        bool listChanged = !string.Equals(signature, _midiDeviceSignature, StringComparison.Ordinal);
        if (listChanged)
        {
            _midiDeviceSignature = signature;
            PublishSettings();
        }

        if (!available)
        {
            if (_midiDeviceWasAvailable)
            {
                PanicMidiTargets();
                _midiDeviceWasAvailable = false;
                _midiOpenFailures = 0;
                _midiOpenRetryAtTicks = 0;
                _ = CloseMidiDeviceAsync(deviceName);
            }

            return;
        }

        if (_midiDeviceWasAvailable || Environment.TickCount64 < _midiOpenRetryAtTicks)
        {
            return;
        }

        _midiDeviceWasAvailable = true;
        _ = OpenMidiDeviceAsync(deviceName);
    }

    private async Task OpenMidiDeviceAsync(string deviceName)
    {
        await TryOpenMidiAsync(deviceName).ConfigureAwait(true);
        if (_disposed ||
            !_settings.MidiEnabled ||
            !string.Equals(_settings.MidiInputDevice, deviceName, StringComparison.Ordinal))
        {
            return;
        }

        bool open = _midi.IsOpen;
        if (open)
        {
            _midiOpenFailures = 0;
            _midiOpenRetryAtTicks = 0;
        }
        else
        {
            // Let the monitor try again after a widening backoff instead of
            // blocking forever on a transient open failure.
            _midiDeviceWasAvailable = false;
            _midiOpenFailures = Math.Min(_midiOpenFailures + 1, 6);
            long backoffMs = Math.Min(60_000L, 5_000L << Math.Min(_midiOpenFailures - 1, 4));
            _midiOpenRetryAtTicks = Environment.TickCount64 + backoffMs;
        }

        SetStatus(
            open
                ? $"MIDI input '{deviceName}' connected."
                : $"Could not open MIDI input '{deviceName}'. Retrying.",
            isError: !open);
        PublishSettings();
    }

    private async Task CloseMidiDeviceAsync(string deviceName)
    {
        try
        {
            await _midi.CloseAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not close the MIDI input: " + ex.Message);
        }

        if (_disposed)
        {
            return;
        }

        _midi.SetError($"MIDI input '{deviceName}' disconnected.");
        SetStatus($"MIDI input '{deviceName}' disconnected.", isError: true);
        PublishSettings();
    }

    private string MidiDeviceSignature() =>
        string.Join("|", _midi.Devices.Select(device => device.Name));

    private async Task RefreshMidiDevicesAsync()
    {
        await Task.Run(_midi.RefreshDevices).ConfigureAwait(true);
        if (!_disposed)
        {
            _midiDeviceSignature = MidiDeviceSignature();
            PublishSettings();
        }
    }

    private void OnMidiMessageReceived(MidiInputEvent message)
    {
        if (_disposed)
        {
            return;
        }

        RouteMidiToVst3(message);

        _midiUiQueue.Enqueue(message);
        if (Interlocked.Exchange(ref _midiUiScheduled, 1) == 0)
        {
            try
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainMidiUiQueue));
            }
            catch
            {
                Interlocked.Exchange(ref _midiUiScheduled, 0);
            }
        }
    }

    private void DrainMidiUiQueue()
    {
        if (_disposed)
        {
            return;
        }

        int processed = 0;
        while (processed < 512 && _midiUiQueue.TryDequeue(out var message))
        {
            try
            {
                ApplyMidiMessage(message);
            }
            catch (Exception ex)
            {
                AppLog.Warning("MIDI mapping failed: " + ex.Message);
            }

            processed++;
        }

        Interlocked.Exchange(ref _midiUiScheduled, 0);
        if (!_midiUiQueue.IsEmpty && Interlocked.Exchange(ref _midiUiScheduled, 1) == 0)
        {
            try
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainMidiUiQueue));
            }
            catch
            {
                Interlocked.Exchange(ref _midiUiScheduled, 0);
            }
        }
    }

    private void RouteMidiToVst3(MidiInputEvent message)
    {
        Vst3MidiTarget[] targets = Volatile.Read(ref _vst3MidiTargets);
        if (targets.Length == 0)
        {
            return;
        }

        byte channel = (byte)Math.Clamp(message.Channel, 0, 15);
        byte number = (byte)Math.Clamp(message.Number, 0, 127);
        byte value = (byte)Math.Clamp(message.Value, 0, 127);
        byte status = message.Kind == MidiInputKind.Note
            ? value == 0 ? (byte)(0x80 | channel) : (byte)(0x90 | channel)
            : (byte)(0xB0 | channel);

        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (!MidiRouting.AcceptsChannel(target.Channel, message.Channel))
            {
                continue;
            }

            target.Processor.EnqueueMidi(status, number, value);
        }
    }

    private void OnMidiClockReceived()
    {
        long now = Stopwatch.GetTimestamp();
        if (_midiLastClockTimestamp != 0 &&
            now - _midiLastClockTimestamp > Stopwatch.Frequency)
        {
            _midiClockIndex = 0;
            _midiClockCount = 0;
        }

        _midiLastClockTimestamp = now;
        _midiClockTicks[_midiClockIndex] = now;
        _midiClockIndex = (_midiClockIndex + 1) % MidiClockWindow;
        if (_midiClockCount < MidiClockWindow)
        {
            _midiClockCount++;
        }

        if (_midiClockCount < MidiClockWindow ||
            now - _midiLastTempoTimestamp < Stopwatch.Frequency / 4)
        {
            return;
        }

        long oldest = _midiClockTicks[_midiClockIndex];
        long elapsed = now - oldest;
        if (elapsed <= 0)
        {
            return;
        }

        double bpm = Math.Clamp(
            60.0 * Stopwatch.Frequency / elapsed,
            20.0,
            300.0);
        _midiLastTempoTimestamp = now;
        Vst3MidiTarget[] targets = Volatile.Read(ref _vst3MidiTargets);
        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i].TransportEnabled)
            {
                targets[i].Processor.SetTempo(bpm);
            }
        }

        QueueTimelineTempo(bpm);
    }

    private void QueueTimelineTempo(double bpm)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_disposed && _timeline != null)
                {
                    TimelineSetTempo(bpm);
                }
            }));
        }
        catch
        {
        }
    }

    private void OnMidiTransportReceived(MidiTransportKind kind)
    {
        if (Volatile.Read(ref _storageChangeBusy) != 0)
        {
            return;
        }

        bool playing = kind != MidiTransportKind.Stop;
        Vst3MidiTarget[] targets = Volatile.Read(ref _vst3MidiTargets);
        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (!target.TransportEnabled)
            {
                continue;
            }

            if (kind == MidiTransportKind.Start)
            {
                target.Processor.ResetTransportPosition();
            }

            target.Processor.SetTransportState(playing);
        }

        if (!playing)
        {
            PanicMidiTargets();
        }

        try
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (_timeline != null)
                {
                    if (kind == MidiTransportKind.Start)
                    {
                        _timeline.Seek(0);
                    }

                    if (playing)
                    {
                        TimelinePlay();
                    }
                    else
                    {
                        TimelinePause();
                    }
                }

                if (playing)
                {
                    StartRouting();
                }
                else
                {
                    StopRouting();
                }
            }));
        }
        catch
        {
        }
    }

    private static void SendAllNotesOff(IVst3Processor processor) =>
        MidiRouting.EnqueueAllNotesOff((status, data1, data2) =>
            processor.EnqueueMidi(status, data1, data2));

    private void PanicMidiTargets()
    {
        Vst3MidiTarget[] targets = Volatile.Read(ref _vst3MidiTargets);
        for (int i = 0; i < targets.Length; i++)
        {
            SendAllNotesOff(targets[i].Processor);
        }
    }

    private void RefreshVst3MidiTargets()
    {
        Vst3MidiTarget[] targets = _nodes.Values
            .Where(entry => entry.Vst3MidiEnabled)
            .Select(entry => entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault() is { } processor
                ? new Vst3MidiTarget(processor, entry.Vst3MidiChannel, entry.Vst3TransportEnabled)
                : null)
            .Where(target => target != null)
            .Select(target => target!)
            .ToArray();
        Volatile.Write(ref _vst3MidiTargets, targets);
    }

    private void SetVst3Midi(string id, bool enabled)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault() is not { } processor)
        {
            return;
        }

        if (!enabled)
        {
            SendAllNotesOff(processor);
        }

        entry.Vst3MidiEnabled = enabled;
        entry.Vst3MidiConfigured = true;
        RefreshVst3MidiTargets();
        SetStatus(enabled ? $"{entry.Title}: receiving MIDI input." : $"{entry.Title}: MIDI input disabled.");
        PublishGraph();
    }

    private void SetVst3MidiChannel(string id, int channel)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault() is not { } processor)
        {
            return;
        }

        SendAllNotesOff(processor);
        entry.Vst3MidiChannel = Math.Clamp(channel, -1, 15);
        entry.Vst3MidiConfigured = true;
        RefreshVst3MidiTargets();
        SetStatus(entry.Vst3MidiChannel < 0
            ? $"{entry.Title}: receiving all MIDI channels."
            : $"{entry.Title}: receiving MIDI channel {entry.Vst3MidiChannel + 1}.");
        PublishGraph();
    }

    private void SetVst3Transport(string id, bool enabled)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault() == null)
        {
            return;
        }

        entry.Vst3TransportEnabled = enabled;
        entry.Vst3MidiConfigured = true;
        RefreshVst3MidiTargets();
        SetStatus(enabled
            ? $"{entry.Title}: follows MIDI clock and transport."
            : $"{entry.Title}: ignores MIDI clock and transport.");
        PublishGraph();
    }

    private void ApplyMidiMessage(MidiInputEvent message)
    {
        if (_midiLearnTarget is { } target)
        {
            if (message.Kind == MidiInputKind.Note && message.Value == 0)
            {
                return;
            }

            bool parameterTarget = string.Equals(target.Target, "parameter", StringComparison.Ordinal);
            AudioParameter? learned = null;
            if (parameterTarget)
            {
                learned = ResolveMidiParameter(target.NodeId, target.ProcessorIndex, target.ParameterIndex);
                if (learned == null)
                {
                    _midiLearnTarget = null;
                    return;
                }
            }
            else if (!IsNodeMidiTarget(target.NodeId, target.Target))
            {
                _midiLearnTarget = null;
                return;
            }

            var binding = new MidiBindingEntry
            {
                NodeId = target.NodeId,
                Target = target.Target,
                ProcessorIndex = target.ProcessorIndex,
                ParameterIndex = target.ParameterIndex,
                Channel = message.Channel,
                Kind = message.Kind,
                Number = message.Number,
                Min = learned == null ? 0f : learned.IsToggle ? 0f : learned.Min,
                Max = learned == null
                    ? string.Equals(target.Target, "gain", StringComparison.Ordinal) ? 1.5f : 1f
                    : learned.IsToggle ? 1f : learned.Max,
            };

            _midiBindings.RemoveAll(item =>
                item.NodeId == binding.NodeId &&
                string.Equals(item.Target, binding.Target, StringComparison.Ordinal) &&
                item.ProcessorIndex == binding.ProcessorIndex &&
                item.ParameterIndex == binding.ParameterIndex);
            _midiBindings.Add(binding);
            _midiLearnTarget = null;
            SetStatus($"MIDI bound to {binding.Label}.");
            PublishGraph();
            return;
        }

        foreach (var binding in _midiBindings)
        {
            if (binding.Kind != message.Kind || binding.Number != message.Number)
            {
                continue;
            }

            if (binding.Channel >= 0 && binding.Channel != message.Channel)
            {
                continue;
            }

            if (!string.Equals(binding.Target, "parameter", StringComparison.Ordinal))
            {
                ApplyNodeMidiBinding(binding, message);
                continue;
            }

            if (ResolveMidiParameter(binding.NodeId, binding.ProcessorIndex, binding.ParameterIndex) is not { } parameter)
            {
                continue;
            }

            float min = ClampMidiBound(binding.Min, parameter, isMin: true);
            float max = ClampMidiBound(binding.Max, parameter, isMin: false);
            if (min > max)
            {
                (min, max) = (max, min);
            }

            if (message.Kind == MidiInputKind.Note)
            {
                if (parameter.IsToggle)
                {
                    if (message.Value > 0)
                    {
                        parameter.Set(parameter.Get() >= 0.5f ? 0f : 1f);
                        QueueMidiParameter(binding);
                    }

                    continue;
                }

                parameter.Set(message.Value > 0 ? max : min);
            }
            else if (parameter.IsToggle)
            {
                parameter.Set(message.Value >= 64 ? 1f : 0f);
            }
            else
            {
                float normalized = Math.Clamp(message.Value / 127f, 0f, 1f);
                parameter.Set(min + (max - min) * normalized);
            }

            QueueMidiParameter(binding);
        }
    }

    private void QueueMidiParameter(MidiBindingEntry binding)
    {
        lock (_midiParameterLock)
        {
            _midiParameterChanges.Add(
                (binding.NodeId, binding.ProcessorIndex, binding.ParameterIndex));
        }
    }

    private bool IsNodeMidiTarget(string nodeId, string target)
    {
        if (!_nodes.TryGetValue(nodeId, out var entry) ||
            (entry.Source == null && entry.Output == null))
        {
            return false;
        }

        return target is "gain" or "mute";
    }

    private void ApplyNodeMidiBinding(MidiBindingEntry binding, MidiInputEvent message)
    {
        if (message.Kind == MidiInputKind.Note && message.Value == 0)
        {
            return;
        }

        if (!_nodes.TryGetValue(binding.NodeId, out var entry))
        {
            return;
        }

        if (string.Equals(binding.Target, "gain", StringComparison.Ordinal))
        {
            float normalized = message.Kind == MidiInputKind.Note
                ? message.Value > 0 ? 1f : 0f
                : Math.Clamp(message.Value / 127f, 0f, 1f);
            float gain = Math.Clamp(binding.Min + (binding.Max - binding.Min) * normalized, 0f, 2f);
            if (entry.Source != null)
            {
                entry.Source.Gain = gain;
            }
            else if (entry.Output != null)
            {
                entry.Output.Volume = gain;
            }

            PublishNodeState(entry.Id);
            return;
        }

        if (string.Equals(binding.Target, "mute", StringComparison.Ordinal))
        {
            bool muted = message.Kind == MidiInputKind.Note
                ? !(entry.Source?.Muted ?? entry.Output?.Muted ?? false)
                : message.Value >= 64;
            ApplyNodeMute(entry.Id, muted, publish: true);
        }
    }

    private AudioParameter? ResolveMidiParameter(string nodeId, int processorIndex, int parameterIndex)
    {
        if (!_nodes.TryGetValue(nodeId, out var entry) ||
            entry.Modifier == null ||
            processorIndex < 0 ||
            processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 ||
            parameterIndex >= parameterized.Parameters.Count)
        {
            return null;
        }

        return parameterized.Parameters[parameterIndex];
    }

    private void StartMidiLearn(string nodeId, int processorIndex, int parameterIndex)
    {
        if (!_settings.MidiEnabled)
        {
            SetStatus("Enable MIDI input in Settings first.", isError: true);
            return;
        }

        if (!_midi.IsOpen)
        {
            SetStatus("No MIDI input is open. Check Settings > MIDI.", isError: true);
            return;
        }

        if (ResolveMidiParameter(nodeId, processorIndex, parameterIndex) is not { } parameter)
        {
            SetStatus("That parameter cannot be MIDI-mapped.", isError: true);
            return;
        }

        _midiLearnTarget = new MidiLearnTarget(nodeId, processorIndex, parameterIndex);
        SetStatus($"Move a MIDI control or press a note to bind {parameter.Name}.");
        PublishGraph();
    }

    private void StartNodeMidiLearn(string nodeId, string target)
    {
        if (!_settings.MidiEnabled)
        {
            SetStatus("Enable MIDI input in Settings first.", isError: true);
            return;
        }

        if (!_midi.IsOpen)
        {
            SetStatus("No MIDI input is open. Check Settings > MIDI.", isError: true);
            return;
        }

        if (!IsNodeMidiTarget(nodeId, target))
        {
            SetStatus("That node control cannot be MIDI-mapped.", isError: true);
            return;
        }

        _midiLearnTarget = new MidiLearnTarget(nodeId, -1, -1, target);
        SetStatus($"Move a MIDI control or press a note to bind {target}.");
        PublishGraph();
    }

    private void ClearNodeMidiBinding(string nodeId, string target)
    {
        int removed = _midiBindings.RemoveAll(item =>
            item.NodeId == nodeId &&
            string.Equals(item.Target, target, StringComparison.Ordinal));
        if (_midiLearnTarget is { } current &&
            current.NodeId == nodeId &&
            string.Equals(current.Target, target, StringComparison.Ordinal))
        {
            _midiLearnTarget = null;
        }

        if (removed > 0)
        {
            SetStatus("MIDI binding cleared.");
            PublishGraph();
        }
    }

    private void ClearMidiBinding(string nodeId, int processorIndex, int parameterIndex)
    {
        int removed = _midiBindings.RemoveAll(item =>
            item.NodeId == nodeId &&
            string.Equals(item.Target, "parameter", StringComparison.Ordinal) &&
            item.ProcessorIndex == processorIndex &&
            item.ParameterIndex == parameterIndex);
        if (_midiLearnTarget is { } target &&
            target.NodeId == nodeId &&
            string.Equals(target.Target, "parameter", StringComparison.Ordinal) &&
            target.ProcessorIndex == processorIndex &&
            target.ParameterIndex == parameterIndex)
        {
            _midiLearnTarget = null;
        }

        if (removed > 0)
        {
            SetStatus("MIDI binding cleared.");
            PublishGraph();
        }
    }

    private string? MidiBindingLabel(string nodeId, int processorIndex, int parameterIndex) =>
        _midiBindings.FirstOrDefault(item =>
            item.NodeId == nodeId &&
            string.Equals(item.Target, "parameter", StringComparison.Ordinal) &&
            item.ProcessorIndex == processorIndex &&
            item.ParameterIndex == parameterIndex)?.Label;

    private bool IsMidiLearning(string nodeId, int processorIndex, int parameterIndex) =>
        _midiLearnTarget is { } target &&
        target.NodeId == nodeId &&
        string.Equals(target.Target, "parameter", StringComparison.Ordinal) &&
        target.ProcessorIndex == processorIndex &&
        target.ParameterIndex == parameterIndex;

    private string? NodeMidiBindingLabel(string nodeId, string target) =>
        _midiBindings.FirstOrDefault(item =>
            item.NodeId == nodeId &&
            string.Equals(item.Target, target, StringComparison.Ordinal))?.Label;

    private bool IsNodeMidiLearning(string nodeId, string target) =>
        _midiLearnTarget is { } current &&
        current.NodeId == nodeId &&
        string.Equals(current.Target, target, StringComparison.Ordinal);

    private void ClearMidiBindings()
    {
        _midiBindings.Clear();
        _midiLearnTarget = null;
        lock (_midiParameterLock)
        {
            _midiParameterChanges.Clear();
        }
    }

    private List<MidiBindingSnapshot> SnapshotMidiBindings(string nodeId) =>
        _midiBindings
            .Where(binding => string.Equals(binding.NodeId, nodeId, StringComparison.Ordinal))
            .Select(binding => new MidiBindingSnapshot(
                binding.Target,
                binding.ProcessorIndex,
                binding.ParameterIndex,
                binding.Channel,
                binding.Kind,
                binding.Number,
                binding.Min,
                binding.Max))
            .ToList();

    private void RestoreMidiBindingsForNode(
        NodeEntry entry,
        IReadOnlyList<MidiBindingSnapshot>? bindings)
    {
        _midiBindings.RemoveAll(binding => string.Equals(binding.NodeId, entry.Id, StringComparison.Ordinal));
        if (bindings == null)
        {
            return;
        }

        foreach (var binding in bindings)
        {
            _midiBindings.Add(new MidiBindingEntry
            {
                NodeId = entry.Id,
                Target = binding.Target,
                ProcessorIndex = binding.ProcessorIndex,
                ParameterIndex = binding.ParameterIndex,
                Channel = binding.Channel,
                Kind = binding.Kind,
                Number = binding.Number,
                Min = binding.Min,
                Max = binding.Max,
            });
        }
    }

    private void SaveMidiBindings(
        Preset preset,
        Dictionary<string, int> modifierIndex,
        Dictionary<string, int> sourceIndex,
        Dictionary<string, int> outputIndex)
    {
        foreach (var binding in _midiBindings)
        {
            var saved = new PresetMidiBinding
            {
                Target = binding.Target,
                ProcessorIndex = binding.ProcessorIndex,
                ParameterIndex = binding.ParameterIndex,
                Channel = binding.Channel,
                Kind = binding.Kind == MidiInputKind.Note ? "note" : "cc",
                Number = binding.Number,
                Min = binding.Min,
                Max = binding.Max,
            };

            if (string.Equals(binding.Target, "parameter", StringComparison.Ordinal))
            {
                if (!modifierIndex.TryGetValue(binding.NodeId, out int modifier))
                {
                    continue;
                }

                saved.ModifierIndex = modifier;
            }
            else if (sourceIndex.TryGetValue(binding.NodeId, out int source))
            {
                saved.SourceIndex = source;
            }
            else if (outputIndex.TryGetValue(binding.NodeId, out int output))
            {
                saved.OutputIndex = output;
            }
            else
            {
                continue;
            }

            preset.MidiBindings.Add(saved);
        }
    }

    private void RestoreMidiBindings(
        Preset preset,
        List<NodeEntry?> modifiers,
        List<NodeEntry?> sources,
        List<NodeEntry?> outputs)
    {
        foreach (var saved in preset.MidiBindings)
        {
            string target = string.IsNullOrWhiteSpace(saved.Target) ? "parameter" : saved.Target;
            MidiInputKind kind = string.Equals(saved.Kind, "note", StringComparison.OrdinalIgnoreCase)
                ? MidiInputKind.Note
                : MidiInputKind.ControlChange;
            if (!string.Equals(target, "parameter", StringComparison.Ordinal))
            {
                NodeEntry? node = target is "gain" or "mute"
                    ? saved.SourceIndex >= 0
                        ? Resolve(sources, saved.SourceIndex)
                        : Resolve(outputs, saved.OutputIndex)
                    : null;
                if (node == null || !IsNodeMidiTarget(node.Id, target))
                {
                    continue;
                }

                _midiBindings.Add(new MidiBindingEntry
                {
                    NodeId = node.Id,
                    Target = target,
                    Channel = saved.Channel,
                    Kind = kind,
                    Number = saved.Number,
                    Min = 0f,
                    Max = target == "gain" ? 1.5f : 1f,
                });
                continue;
            }

            var modifier = Resolve(modifiers, saved.ModifierIndex);
            if (modifier == null ||
                saved.ProcessorIndex < 0 ||
                saved.ParameterIndex < 0 ||
                modifier.Modifier == null ||
                saved.ProcessorIndex >= modifier.Modifier.Processors.Count ||
                modifier.Modifier.Processors[saved.ProcessorIndex] is not IParameterizedProcessor parameterized ||
                saved.ParameterIndex >= parameterized.Parameters.Count)
            {
                continue;
            }

            var parameter = parameterized.Parameters[saved.ParameterIndex];
            float min = ClampMidiBound(saved.Min, parameter, isMin: true);
            float max = ClampMidiBound(saved.Max, parameter, isMin: false);
            if (min > max)
            {
                (min, max) = (max, min);
            }

            _midiBindings.Add(new MidiBindingEntry
            {
                NodeId = modifier.Id,
                ProcessorIndex = saved.ProcessorIndex,
                ParameterIndex = saved.ParameterIndex,
                Channel = saved.Channel,
                Kind = kind,
                Number = saved.Number,
                Min = min,
                Max = max,
            });
        }
    }

    private static float ClampMidiBound(float value, AudioParameter parameter, bool isMin)
    {
        float fallback = isMin ? parameter.Min : parameter.Max;
        return float.IsFinite(value) ? Math.Clamp(value, parameter.Min, parameter.Max) : fallback;
    }

    private void FlushMidiParameterChanges()
    {
        (string NodeId, int ProcessorIndex, int ParameterIndex)[] changes;
        lock (_midiParameterLock)
        {
            if (_midiParameterChanges.Count == 0)
            {
                return;
            }

            changes = _midiParameterChanges.ToArray();
            _midiParameterChanges.Clear();
        }

        for (int i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            PublishParameterChange(change.NodeId, change.ProcessorIndex, change.ParameterIndex);
        }
    }
}
