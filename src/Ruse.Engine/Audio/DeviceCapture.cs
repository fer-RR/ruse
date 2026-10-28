using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Captures a physical capture endpoint (microphone, line-in, etc).
/// </summary>
public sealed class DeviceCapture : IDisposable
{
    private readonly WasapiRecorder _capture;
    private readonly string _deviceFriendlyName;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat => _capture.WaveFormat;
    public string DeviceId { get; }
    public string Name => _deviceFriendlyName;

    /// <summary>True when the endpoint is actually running in IAudioClient3 low-latency mode.</summary>
    public bool LowLatencyActive => _capture.LowLatencyActive;

    public DeviceCapture(string deviceId)
    {
        DeviceId = deviceId;
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        _deviceFriendlyName = device.FriendlyName;
        _capture = BuildRecorder(device);
        _capture.DataAvailable += OnRecorderData;
        _capture.RecordingStopped += (s, e) => RecordingStopped?.Invoke(this, e);
    }

    public DeviceCapture(MMDevice device)
    {
        DeviceId = device.ID;
        _deviceFriendlyName = device.FriendlyName;
        _capture = BuildRecorder(device);
        _capture.DataAvailable += OnRecorderData;
        _capture.RecordingStopped += (s, e) => RecordingStopped?.Invoke(this, e);
    }

    // The new recorder hands us a span, but the rest of the pipeline (and the
    // process-loopback capture) speaks WaveInEventArgs, so bridge the two here
    // using a reusable buffer to keep the steady state allocation-free.
    private byte[] _packetBuffer = new byte[16384];
    private WaveInEventArgs? _packetArgs;

    private void OnRecorderData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        if (_packetBuffer.Length < buffer.Length)
        {
            _packetBuffer = new byte[Math.Max(buffer.Length, _packetBuffer.Length * 2)];
            _packetArgs = null;
        }

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            Array.Clear(_packetBuffer, 0, buffer.Length);
        }
        else
        {
            buffer.CopyTo(_packetBuffer);
        }

        _packetArgs = new WaveInEventArgs(_packetBuffer, buffer.Length);
        DataAvailable?.Invoke(this, _packetArgs);
    }

    private static WasapiRecorder BuildRecorder(MMDevice device)
    {
        // Capture endpoints use event-driven shared mode. Low latency is only
        // honoured when the capture format matches the device mix format, which
        // is exactly what we capture, so request it and let the recorder fall
        // back silently if the device won't grant it.
        return new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency()
            .Build();
    }

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try
        {
            _capture.StopRecording();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }
}
