using System.IO.MemoryMappedFiles;

namespace Ruse.App.Audio;

/// <summary>
/// Shared-memory audio transport for the isolated VST3 host. The named pipe is
/// kept for process lifetime/control; PCM blocks move through this mapping so
/// the host does not pay a pipe round-trip for every render block.
/// </summary>
internal sealed class Vst3SharedAudio : IDisposable
{
    public const int MaxFrames = 2048;
    public const int Channels = 2;
    public const int MaxMidiEvents = 128;

    private const int HeaderBytes = 40;
    private const int TempoOffset = 24;
    private const int TransportOffset = 32;
    private const int TransportResetOffset = 36;
    private const int InputOffset = HeaderBytes;
    private const int SampleBytes = MaxFrames * Channels * sizeof(float);
    private const int OutputOffset = InputOffset + SampleBytes;
    private const int MidiOffset = OutputOffset + SampleBytes;
    private const int MidiEventBytes = 16;
    private const int Capacity = MidiOffset + MaxMidiEvents * MidiEventBytes;
    private const int TimeoutMs = 150;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _inputReady;
    private readonly EventWaitHandle _outputReady;

    private Vst3SharedAudio(string name, bool owner)
    {
        _file = owner
            ? MemoryMappedFile.CreateOrOpen(name + ".shm", Capacity)
            : MemoryMappedFile.OpenExisting(name + ".shm");
        _view = _file.CreateViewAccessor(0, Capacity);
        _inputReady = owner
            ? new EventWaitHandle(false, EventResetMode.AutoReset, name + ".in")
            : EventWaitHandle.OpenExisting(name + ".in");
        _outputReady = owner
            ? new EventWaitHandle(false, EventResetMode.AutoReset, name + ".out")
            : EventWaitHandle.OpenExisting(name + ".out");
        if (owner)
        {
            _view.Write(TempoOffset, BitConverter.DoubleToInt64Bits(120.0));
            _view.Write(TransportOffset, 0);
            _view.Write(TransportResetOffset, 0);
        }
    }

    public void SetTempo(double bpm)
    {
        if (double.IsFinite(bpm) && bpm > 0)
        {
            _view.Write(TempoOffset, BitConverter.DoubleToInt64Bits(bpm));
        }
    }

    public double ReadTempo()
    {
        long bits = _view.ReadInt64(TempoOffset);
        double bpm = BitConverter.Int64BitsToDouble(bits);
        return double.IsFinite(bpm) && bpm > 0 ? bpm : 120.0;
    }

    public void SetTransportState(bool playing) =>
        _view.Write(TransportOffset, playing ? 1 : 0);

    public bool ReadTransportState() => _view.ReadInt32(TransportOffset) != 0;

    public void ResetTransportPosition() =>
        _view.Write(TransportResetOffset, _view.ReadInt32(TransportResetOffset) + 1);

    public void WriteTransportReset(int value) => _view.Write(TransportResetOffset, value);

    public int ReadTransportReset() => _view.ReadInt32(TransportResetOffset);

    public static Vst3SharedAudio Create(string name) => new(name, owner: true);

    public static Vst3SharedAudio Open(string name) => new(name, owner: false);

    public bool Process(float[] samples, int frames) =>
        Process(samples, frames, ReadOnlySpan<Vst3MidiEvent>.Empty);

    public bool Process(float[] samples, int frames, ReadOnlySpan<Vst3MidiEvent> midi)
    {
        if (frames <= 0 || frames > MaxFrames || samples.Length < frames * Channels)
        {
            return false;
        }

        int midiCount = Math.Min(midi.Length, MaxMidiEvents);
        _view.Write(0, 1);
        _view.Write(4, frames);
        _view.Write(8, 0);
        _view.Write(12, Channels);
        _view.Write(16, midiCount);
        _view.Write(20, 0);
        _view.WriteArray(InputOffset, samples, 0, frames * Channels);
        for (int i = 0; i < midiCount; i++)
        {
            Vst3MidiEvent value = midi[i];
            int offset = MidiOffset + i * MidiEventBytes;
            _view.Write(offset, (int)value.Status);
            _view.Write(offset + 4, (int)value.Data1);
            _view.Write(offset + 8, (int)value.Data2);
            _view.Write(offset + 12, value.SampleOffset);
        }

        _inputReady.Set();

        if (!_outputReady.WaitOne(TimeoutMs))
        {
            return false;
        }

        int outputFrames = _view.ReadInt32(8);
        int outputChannels = _view.ReadInt32(12);
        if (outputFrames <= 0 || outputFrames > frames || outputChannels != Channels)
        {
            return false;
        }

        _view.ReadArray(OutputOffset, samples, 0, outputFrames * outputChannels);
        return true;
    }

    public bool Receive(float[] destination, out int frames) =>
        Receive(destination, Span<Vst3MidiEvent>.Empty, out frames, out _);

    public bool Receive(
        float[] destination,
        Span<Vst3MidiEvent> midi,
        out int frames,
        out int midiCount)
    {
        frames = 0;
        midiCount = 0;
        if (!_inputReady.WaitOne(Timeout.Infinite))
        {
            return false;
        }

        frames = _view.ReadInt32(4);
        int channels = _view.ReadInt32(12);
        int count = _view.ReadInt32(16);
        if (_view.ReadInt32(0) != 1 ||
            frames <= 0 || frames > MaxFrames || channels != Channels ||
            destination.Length < frames * channels ||
            count < 0 || count > MaxMidiEvents || count > midi.Length)
        {
            return false;
        }

        _view.ReadArray(InputOffset, destination, 0, frames * channels);
        for (int i = 0; i < count; i++)
        {
            int offset = MidiOffset + i * MidiEventBytes;
            midi[i] = new Vst3MidiEvent(
                (byte)_view.ReadInt32(offset),
                (byte)_view.ReadInt32(offset + 4),
                (byte)_view.ReadInt32(offset + 8),
                _view.ReadInt32(offset + 12));
        }

        midiCount = count;
        return true;
    }

    public void Send(float[] samples, int frames)
    {
        _view.Write(8, frames);
        _view.Write(12, Channels);
        _view.Write(16, 0);
        _view.WriteArray(OutputOffset, samples, 0, frames * Channels);
        _outputReady.Set();
    }

    public void Dispose()
    {
        _inputReady.Dispose();
        _outputReady.Dispose();
        _view.Dispose();
        _file.Dispose();
    }
}
