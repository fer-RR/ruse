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
    private MidiDeviceInfo[] _devices = Array.Empty<MidiDeviceInfo>();
    private string? _deviceError;
    private string? _openError;

    // Open and close operations are chained onto a single background queue so a
    // close always finishes before the next open starts, without ever blocking
    // the caller. A hung driver can stall the queue but not the UI.
    private readonly object _queueLock = new();
    private Task _queueTail = Task.CompletedTask;
    private int _openSequence;
    private bool _disposed;

    public event Action<MidiInputEvent>? MessageReceived;
    public event Action? ClockReceived;
    public event Action<MidiTransportKind>? TransportReceived;

    public IReadOnlyList<MidiDeviceInfo> Devices => Volatile.Read(ref _devices);
    public bool IsOpen =>
        Volatile.Read(ref _input) != null || Volatile.Read(ref _testTimer) != null;
    public string? LastError =>
        Volatile.Read(ref _openError) ?? Volatile.Read(ref _deviceError);

    public void RefreshDevices()
    {
        var devices = new List<MidiDeviceInfo>();
        try
        {
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

            Volatile.Write(ref _deviceError, null);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _deviceError, ex.Message);
        }

        devices.Add(new MidiDeviceInfo(-1, TestDeviceName));
        Volatile.Write(ref _devices, devices.ToArray());
    }

    public void SetError(string message) => Volatile.Write(ref _openError, message);

    /// <summary>
    /// Queues an open after any pending close. Returns immediately; await the
    /// task to know when the device is ready.
    /// </summary>
    public Task OpenAsync(string deviceName)
    {
        lock (_queueLock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            // Only the most recent open request has to run. If several arrive
            // while a close is still finishing, the older ones become no-ops.
            int token = ++_openSequence;
            return EnqueueLocked(() =>
            {
                if (token != Volatile.Read(ref _openSequence))
                {
                    return;
                }

                OpenCore(deviceName);
            });
        }
    }

    /// <summary>Queues a close without blocking the caller.</summary>
    public Task CloseAsync()
    {
        lock (_queueLock)
        {
            return EnqueueLocked(CloseCore);
        }
    }

    private Task EnqueueLocked(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queueTail = _queueTail.ContinueWith(
            _ =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return completion.Task;
    }

    private void OpenCore(string deviceName)
    {
        CloseCore();
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
                Volatile.Write(ref _openError, $"MIDI input '{deviceName}' was not found.");
                return;
            }

            if (string.Equals(device.Name, TestDeviceName, StringComparison.Ordinal))
            {
                _testTick = 0;
                Interlocked.Exchange(
                    ref _testTimer,
                    new Timer(EmitTestTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1000.0 / 48)));
                Volatile.Write(ref _openError, null);
                return;
            }

            var input = new MidiIn(device.Index);
            input.MessageReceived += OnMessageReceived;
            input.Start();
            Volatile.Write(ref _input, input);
            Volatile.Write(ref _openError, null);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _openError, ex.Message);
            CloseCore();
        }
    }

    private void CloseCore()
    {
        Interlocked.Exchange(ref _testTimer, null)?.Dispose();

        var input = Interlocked.Exchange(ref _input, null);
        if (input == null)
        {
            return;
        }

        try
        {
            input.MessageReceived -= OnMessageReceived;
            input.Stop();
            input.Dispose();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _openError, "The MIDI device did not close cleanly: " + ex.Message);
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

        if (ParseChannelEvent(midiEvent) is { } value)
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

    /// <summary>
    /// NAudio creates NoteEvent for hardware note-off messages and NoteOnEvent only
    /// for note-ons with non-zero velocity. Handle both shapes here.
    /// </summary>
    public static MidiInputEvent? ParseChannelEvent(MidiEvent? midiEvent) =>
        midiEvent switch
        {
            NoteEvent note when note.CommandCode is MidiCommandCode.NoteOn or MidiCommandCode.NoteOff =>
                new MidiInputEvent(
                    Math.Clamp(note.Channel - 1, 0, 15),
                    MidiInputKind.Note,
                    note.NoteNumber,
                    note.CommandCode == MidiCommandCode.NoteOn ? note.Velocity : 0),
            ControlChangeEvent cc =>
                new MidiInputEvent(
                    Math.Clamp(cc.Channel - 1, 0, 15),
                    MidiInputKind.ControlChange,
                    (int)cc.Controller,
                    cc.ControllerValue),
            _ => null,
        };

    public void Dispose()
    {
        Task close;
        lock (_queueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            close = EnqueueLocked(CloseCore);
        }

        try
        {
            if (!close.Wait(TimeSpan.FromSeconds(2)))
            {
                Volatile.Write(ref _openError, "The MIDI device did not close cleanly.");
            }
        }
        catch
        {
        }
    }
}
