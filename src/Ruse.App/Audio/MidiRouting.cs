namespace Ruse.App.Audio;

public static class MidiRouting
{
    /// <summary>A negative channel filter means omni; otherwise 0-15 must match.</summary>
    public static bool AcceptsChannel(int filter, int channel) =>
        filter < 0 || filter == channel;

    /// <summary>
    /// Sends All Sound Off, All Notes Off and Sustain Off on every channel.
    /// Safe to call when a filter, device or transport changes so a dropped
    /// note-off cannot leave the plugin sounding.
    /// </summary>
    public static void EnqueueAllNotesOff(Action<byte, byte, byte> enqueue)
    {
        for (int channel = 0; channel < 16; channel++)
        {
            byte status = (byte)(0xB0 | channel);
            enqueue(status, 120, 0);
            enqueue(status, 123, 0);
            enqueue(status, 64, 0);
        }
    }
}
