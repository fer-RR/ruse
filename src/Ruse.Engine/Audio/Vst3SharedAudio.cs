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

    private const int HeaderBytes = 16;
    private const int InputOffset = HeaderBytes;
    private const int SampleBytes = MaxFrames * Channels * sizeof(float);
    private const int OutputOffset = InputOffset + SampleBytes;
    private const int Capacity = OutputOffset + SampleBytes;
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
    }

    public static Vst3SharedAudio Create(string name) => new(name, owner: true);

    public static Vst3SharedAudio Open(string name) => new(name, owner: false);

    public bool Process(float[] samples, int frames)
    {
        if (frames <= 0 || frames > MaxFrames || samples.Length < frames * Channels)
        {
            return false;
        }

        _view.Write(0, 1);
        _view.Write(4, frames);
        _view.Write(8, 0);
        _view.Write(12, Channels);
        _view.WriteArray(InputOffset, samples, 0, frames * Channels);
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

    public bool Receive(float[] destination, out int frames)
    {
        frames = 0;
        if (!_inputReady.WaitOne(TimeoutMs * 20))
        {
            return false;
        }

        frames = _view.ReadInt32(4);
        int channels = _view.ReadInt32(12);
        if (_view.ReadInt32(0) != 1 ||
            frames <= 0 || frames > MaxFrames || channels != Channels ||
            destination.Length < frames * channels)
        {
            return false;
        }

        _view.ReadArray(InputOffset, destination, 0, frames * channels);
        return true;
    }

    public void Send(float[] samples, int frames)
    {
        _view.Write(8, frames);
        _view.Write(12, Channels);
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
