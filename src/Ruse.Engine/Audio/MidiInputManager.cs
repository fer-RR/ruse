using NAudio.Midi;

namespace Ruse.App.Audio;

public enum MidiInputKind
{
    Note,
    ControlChange,
}

public readonly record struct MidiInputEvent(int Channel, MidiInputKind Kind, int Number, int Value);

public enum MidiTransportKind
{
    Start,
    Continue,
    Stop,
}

public readonly record struct MidiDeviceInfo(int Index, string Name);

/// <summary>
/// Owns the selected Windows MIDI input and converts note/CC messages into
/// simple events. The callback runs on the MIDI driver thread.
/// </summary>
public sealed class MidiInputManager : IDisposable
{
    public const string TestDeviceName = "Ruse Test MIDI";

    private MidiIn? _input;
    private Timer? _testTimer;
    private int _testTick;

    public event Action<MidiInputEvent>? MessageReceived;
    public event Action? ClockReceived;
    public event Action<MidiTransportKind>? TransportReceived;

    public IReadOnlyList<MidiDeviceInfo> Devices { get; private set; } = Array.Empty<MidiDeviceInfo>();
    public bool IsOpen => _input != null || _testTimer != null;
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

            devices.Add(new MidiDeviceInfo(-1, TestDeviceName));
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

            if (string.Equals(device.Name, TestDeviceName, StringComparison.Ordinal))
            {
                _testTick = 0;
                _testTimer = new Timer(EmitTestTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
                LastError = null;
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
        var testTimer = _testTimer;
        _testTimer = null;
        testTimer?.Dispose();

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

    private void EmitTestTick(object? state)
    {
        int tick = Interlocked.Increment(ref _testTick);
        try { ClockReceived?.Invoke(); } catch { }

        if (tick % 40 == 1)
        {
            try { MessageReceived?.Invoke(new MidiInputEvent(0, MidiInputKind.Note, 60, 100)); } catch { }
        }
        else if (tick % 40 == 20)
        {
            try { MessageReceived?.Invoke(new MidiInputEvent(0, MidiInputKind.Note, 60, 0)); } catch { }
        }
        else if (tick % 40 == 10)
        {
            try { MessageReceived?.Invoke(new MidiInputEvent(0, MidiInputKind.ControlChange, 1, 127)); } catch { }
        }

        if (tick % 200 == 1)
        {
            try { TransportReceived?.Invoke(MidiTransportKind.Start); } catch { }
        }
        else if (tick % 200 == 180)
        {
            try { TransportReceived?.Invoke(MidiTransportKind.Stop); } catch { }
        }
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        MidiEvent? midiEvent = e.MidiEvent;
        if (midiEvent == null)
        {
            return;
        }

        switch (midiEvent.CommandCode)
        {
            case MidiCommandCode.TimingClock:
                try { ClockReceived?.Invoke(); } catch { }
                return;
            case MidiCommandCode.StartSequence:
                try { TransportReceived?.Invoke(MidiTransportKind.Start); } catch { }
                return;
            case MidiCommandCode.ContinueSequence:
                try { TransportReceived?.Invoke(MidiTransportKind.Continue); } catch { }
                return;
            case MidiCommandCode.StopSequence:
                try { TransportReceived?.Invoke(MidiTransportKind.Stop); } catch { }
                return;
        }

        MidiInputEvent? message = midiEvent switch
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
