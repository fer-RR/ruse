using System.Diagnostics;
using System.Windows.Threading;
using Ruse.App.Audio;

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

    private readonly MidiInputManager _midi = new();
    private readonly List<MidiBindingEntry> _midiBindings = new();
    private readonly long[] _midiClockTicks = new long[MidiClockWindow];
    private IVst3Processor[] _vst3MidiTargets = Array.Empty<IVst3Processor>();
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
        _midi.RefreshDevices();
        ConfigureMidi();
    }

    private void ConfigureMidi()
    {
        if (!_settings.MidiEnabled)
        {
            _midi.Close();
            return;
        }

        _midi.RefreshDevices();
        if (string.IsNullOrWhiteSpace(_settings.MidiInputDevice))
        {
            _settings.MidiInputDevice = _midi.Devices
                .FirstOrDefault(device => !string.Equals(
                    device.Name, MidiInputManager.TestDeviceName, StringComparison.Ordinal))
                .Name ?? "";
            _settings.Save();
        }

        _midi.Open(_settings.MidiInputDevice);
    }

    private void OnMidiMessageReceived(MidiInputEvent message)
    {
        if (_disposed)
        {
            return;
        }

        RouteMidiToVst3(message);

        try
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_disposed)
                {
                    ApplyMidiMessage(message);
                }
            }));
        }
        catch
        {
        }
    }

    private void RouteMidiToVst3(MidiInputEvent message)
    {
        IVst3Processor[] targets = Volatile.Read(ref _vst3MidiTargets);
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
            targets[i].EnqueueMidi(status, number, value);
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
        IVst3Processor[] targets = Volatile.Read(ref _vst3MidiTargets);
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i].SetTempo(bpm);
        }
    }

    private void OnMidiTransportReceived(MidiTransportKind kind)
    {
        if (Volatile.Read(ref _storageChangeBusy) != 0)
        {
            return;
        }

        bool playing = kind != MidiTransportKind.Stop;
        IVst3Processor[] targets = Volatile.Read(ref _vst3MidiTargets);
        for (int i = 0; i < targets.Length; i++)
        {
            if (kind == MidiTransportKind.Start)
            {
                targets[i].ResetTransportPosition();
            }

            targets[i].SetTransportState(playing);
        }

        try
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_disposed)
                {
                    return;
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

    private void RefreshVst3MidiTargets()
    {
        IVst3Processor[] targets = _nodes.Values
            .Where(entry => entry.Vst3MidiEnabled)
            .Select(entry => entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault())
            .Where(processor => processor != null)
            .Select(processor => processor!)
            .ToArray();
        Volatile.Write(ref _vst3MidiTargets, targets);
    }

    private void SetVst3Midi(string id, bool enabled)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault() == null)
        {
            return;
        }

        entry.Vst3MidiEnabled = enabled;
        entry.Vst3MidiConfigured = true;
        RefreshVst3MidiTargets();
        SetStatus(enabled ? $"{entry.Title}: receiving MIDI input." : $"{entry.Title}: MIDI input disabled.");
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
