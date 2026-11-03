using NAudio.Midi;

namespace Ruse.App.Audio;

public enum MidiInputKind
{
    Note,
    ControlChange,
}

public readonly record struct MidiInputEvent(int Channel, MidiInputKind Kind, int Number, int Value);

public readonly record struct MidiDeviceInfo(int Index, string Name);

/// <summary>
/// Owns the selected Windows MIDI input and converts note/CC messages into
/// simple events. The callback runs on the MIDI driver thread.
/// </summary>
public sealed class MidiInputManager : IDisposable
{
    private MidiIn? _input;

    public event Action<MidiInputEvent>? MessageReceived;

    public IReadOnlyList<MidiDeviceInfo> Devices { get; private set; } = Array.Empty<MidiDeviceInfo>();
    public bool IsOpen => _input != null;
    public string? LastError { get; private set; }

    public void RefreshDevices()
    {
        try
        {
            var devices = new List<MidiDeviceInfo>();
            int count = MidiIn.NumberOfDevices;
            for (int i = 0; i < count; i++)
            {
                string name = MidiIn.DeviceInfo(i).ProductName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"MIDI {i + 1}";
                }
                devices.Add(new MidiDeviceInfo(i, name));
            }

            Devices = devices;
            LastError = null;
        }
        catch (Exception ex)
        {
            Devices = Array.Empty<MidiDeviceInfo>();
            LastError = ex.Message;
        }
    }

    public void Open(string deviceName)
    {
        Close();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        try
        {
            if (Devices.Count == 0)
            {
                RefreshDevices();
            }

            var device = Devices.FirstOrDefault(item =>
                string.Equals(item.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            if (device.Name == null)
            {
                LastError = $"MIDI input '{deviceName}' was not found.";
                return;
            }

            var input = new MidiIn(device.Index);
            input.MessageReceived += OnMessageReceived;
            input.Start();
            _input = input;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Close();
        }
    }

    public void Close()
    {
        var input = _input;
        _input = null;
        if (input == null)
        {
            return;
        }

        try
        {
            input.Stop();
            input.MessageReceived -= OnMessageReceived;
            input.Dispose();
        }
        catch
        {
        }
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        MidiInputEvent? message = e.MidiEvent switch
        {
            NoteOnEvent note =>
                new MidiInputEvent(Math.Clamp(note.Channel - 1, 0, 15), MidiInputKind.Note, note.NoteNumber,
                    note.CommandCode == MidiCommandCode.NoteOff ? 0 : note.Velocity),
            ControlChangeEvent cc =>
                new MidiInputEvent(Math.Clamp(cc.Channel - 1, 0, 15), MidiInputKind.ControlChange, (int)cc.Controller, cc.ControllerValue),
            _ => null,
        };

        if (message is { } value)
        {
            try
            {
                MessageReceived?.Invoke(value);
            }
            catch
            {
            }
        }
    }

    public void Dispose() => Close();
}
