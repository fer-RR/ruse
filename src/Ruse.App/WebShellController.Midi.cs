using System.Windows.Threading;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private sealed class MidiBindingEntry
    {
        public string NodeId { get; init; } = "";
        public int ProcessorIndex { get; init; }
        public int ParameterIndex { get; init; }
        public int Channel { get; init; }
        public MidiInputKind Kind { get; init; }
        public int Number { get; init; }
        public float Min { get; init; }
        public float Max { get; init; } = 1f;

        public string Label => $"CH{Channel + 1} {(Kind == MidiInputKind.Note ? "NOTE" : "CC")} {Number}";
    }

    private sealed record MidiLearnTarget(string NodeId, int ProcessorIndex, int ParameterIndex);

    private readonly MidiInputManager _midi = new();
    private readonly List<MidiBindingEntry> _midiBindings = new();
    private MidiLearnTarget? _midiLearnTarget;
    private readonly HashSet<(string NodeId, int ProcessorIndex, int ParameterIndex)> _midiParameterChanges = new();
    private readonly object _midiParameterLock = new();

    private void InitializeMidi()
    {
        _midi.MessageReceived += OnMidiMessageReceived;
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
            _settings.MidiInputDevice = _midi.Devices.Count > 0 ? _midi.Devices[0].Name : "";
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

    private void ApplyMidiMessage(MidiInputEvent message)
    {
        if (_midiLearnTarget is { } target)
        {
            if (message.Kind == MidiInputKind.Note && message.Value == 0)
            {
                return;
            }

            if (ResolveMidiParameter(target.NodeId, target.ProcessorIndex, target.ParameterIndex) is not { } learned)
            {
                _midiLearnTarget = null;
                return;
            }

            var binding = new MidiBindingEntry
            {
                NodeId = target.NodeId,
                ProcessorIndex = target.ProcessorIndex,
                ParameterIndex = target.ParameterIndex,
                Channel = message.Channel,
                Kind = message.Kind,
                Number = message.Number,
                Min = learned.IsToggle ? 0f : learned.Min,
                Max = learned.IsToggle ? 1f : learned.Max,
            };

            _midiBindings.RemoveAll(item =>
                item.NodeId == binding.NodeId &&
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

            if (ResolveMidiParameter(binding.NodeId, binding.ProcessorIndex, binding.ParameterIndex) is not { } parameter)
            {
                continue;
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

                parameter.Set(message.Value > 0 ? binding.Max : binding.Min);
            }
            else if (parameter.IsToggle)
            {
                parameter.Set(message.Value >= 64 ? 1f : 0f);
            }
            else
            {
                float normalized = Math.Clamp(message.Value / 127f, 0f, 1f);
                parameter.Set(binding.Min + (binding.Max - binding.Min) * normalized);
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

    private void ClearMidiBinding(string nodeId, int processorIndex, int parameterIndex)
    {
        int removed = _midiBindings.RemoveAll(item =>
            item.NodeId == nodeId &&
            item.ProcessorIndex == processorIndex &&
            item.ParameterIndex == parameterIndex);
        if (_midiLearnTarget is { } target &&
            target.NodeId == nodeId &&
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
            item.ProcessorIndex == processorIndex &&
            item.ParameterIndex == parameterIndex)?.Label;

    private bool IsMidiLearning(string nodeId, int processorIndex, int parameterIndex) =>
        _midiLearnTarget is { } target &&
        target.NodeId == nodeId &&
        target.ProcessorIndex == processorIndex &&
        target.ParameterIndex == parameterIndex;

    private void ClearMidiBindings()
    {
        _midiBindings.Clear();
        _midiLearnTarget = null;
        lock (_midiParameterLock)
        {
            _midiParameterChanges.Clear();
        }
    }

    private void SaveMidiBindings(Preset preset, Dictionary<string, int> modifierIndex)
    {
        foreach (var binding in _midiBindings)
        {
            if (!modifierIndex.TryGetValue(binding.NodeId, out int modifier))
            {
                continue;
            }

            preset.MidiBindings.Add(new PresetMidiBinding
            {
                ModifierIndex = modifier,
                ProcessorIndex = binding.ProcessorIndex,
                ParameterIndex = binding.ParameterIndex,
                Channel = binding.Channel,
                Kind = binding.Kind == MidiInputKind.Note ? "note" : "cc",
                Number = binding.Number,
                Min = binding.Min,
                Max = binding.Max,
            });
        }
    }

    private void RestoreMidiBindings(Preset preset, List<NodeEntry?> modifiers)
    {
        foreach (var saved in preset.MidiBindings)
        {
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

            _midiBindings.Add(new MidiBindingEntry
            {
                NodeId = modifier.Id,
                ProcessorIndex = saved.ProcessorIndex,
                ParameterIndex = saved.ParameterIndex,
                Channel = saved.Channel,
                Kind = string.Equals(saved.Kind, "note", StringComparison.OrdinalIgnoreCase)
                    ? MidiInputKind.Note
                    : MidiInputKind.ControlChange,
                Number = saved.Number,
                Min = saved.Min,
                Max = saved.Max,
            });
        }
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
