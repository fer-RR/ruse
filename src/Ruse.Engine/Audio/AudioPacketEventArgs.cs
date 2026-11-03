namespace Ruse.App.Audio;

/// <summary>
/// Reusable capture packet payload. The capture thread sets the buffer and
/// length before every event, so the steady state does not allocate a new
/// event-args object for each audio block.
/// </summary>
public sealed class AudioPacketEventArgs : EventArgs
{
    public byte[] Buffer { get; private set; } = Array.Empty<byte>();
    public int BytesRecorded { get; private set; }
    public ReadOnlySpan<byte> BufferSpan => Buffer.AsSpan(0, BytesRecorded);

    public void Set(byte[] buffer, int bytesRecorded)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
    }
}
