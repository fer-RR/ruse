using NAudio.Wave;
using NAudio.Wave.Asio;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Captures the selected input channels of an ASIO driver.
/// </summary>
/// <remarks>
/// ASIO drivers are exclusive, so a driver can only be opened by one owner.
/// When the same driver is already open as an output, opening it again fails
/// and the reason is reported instead of silently producing silence.
/// </remarks>
public sealed class AsioCapture : IDisposable
{
    private readonly string _driverName;
    private readonly int[] _channels;
    private AsioDevice? _device;
    private bool _disposed;

    public int SampleRate { get; private set; }
    public int ChannelCount => _channels.Length;

    /// <summary>Number of frames captured since <see cref="Start"/>.</summary>
    public long CapturedFrames { get; private set; }

    /// <summary>Raised for every captured block, as interleaved stereo float.</summary>
    public event Action<AsioCapture, byte[]>? DataAvailable;

    /// <summary>Raised when the driver stops by itself.</summary>
    public event Action<AsioCapture, string>? Stopped;

    public AsioCapture(string driverName, int[] channels)
    {
        _driverName = driverName;
        _channels = channels.Length > 0 ? channels : new[] { 0, 1 };
    }

    /// <summary>Opens the driver and starts recording.</summary>
    public void Start()
    {
        _device = AsioDevice.Open(_driverName);

        var options = new AsioRecordingOptions
        {
            InputChannels = _channels,
        };

        _device.AudioCaptured += OnAudioCaptured;
        _device.Stopped += OnDriverStopped;

        try
        {
            _device.InitRecording(options);
        }
        catch
        {
            _device.AudioCaptured -= OnAudioCaptured;
            _device.Stopped -= OnDriverStopped;
            throw;
        }

        SampleRate = _device.CurrentSampleRate;
    }

    // Interleaved float scratch, reused for every block so the capture thread
    // does not allocate per callback.
    private float[] _interleaved = new float[4096];
    private byte[] _bytes = new byte[4096 * 4];

    private void OnAudioCaptured(object? sender, AsioAudioCapturedEventArgs e)
    {
        int frames = e.Frames;
        int channels = _channels.Length;
        if (frames <= 0)
        {
            return;
        }

        int needed = frames * channels;
        if (_interleaved.Length < needed)
        {
            _interleaved = new float[needed];
            _bytes = new byte[needed * 4];
        }

        try
        {
            for (int c = 0; c < channels; c++)
            {
                var span = e.GetChannel(_channels[c]);
                for (int f = 0; f < frames; f++)
                {
                    _interleaved[(f * channels) + c] = f < span.Length ? span[f] : 0f;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ASIO capture read failed: " + ex.Message);
            return;
        }

        Buffer.BlockCopy(_interleaved, 0, _bytes, 0, needed * 4);
        CapturedFrames += frames;

        var handler = DataAvailable;
        if (handler != null)
        {
            handler(this, _bytes);
        }
    }

    private void OnDriverStopped(object? sender, NAudio.Wave.StoppedEventArgs e)
    {
        Stopped?.Invoke(this, e.Exception?.Message ?? "ASIO driver stopped");
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
            if (_device != null)
            {
                _device.AudioCaptured -= OnAudioCaptured;
                _device.Stopped -= OnDriverStopped;
                _device.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ASIO capture close failed: " + ex.Message);
        }

        _device = null;
    }
}
