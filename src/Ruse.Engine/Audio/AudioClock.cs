namespace Ruse.App.Audio;

/// <summary>
/// Shared estimate of the output device clock relative to the 48 kHz engine.
/// Source converters use it to aim at the device that is actually consuming
/// audio instead of assuming every clock is exactly 48 kHz.
/// </summary>
internal static class AudioClock
{
    private static double _outputPpm;

    public static double OutputPpm => Volatile.Read(ref _outputPpm);

    public static void ReportOutputPpm(double ppm)
    {
        if (!double.IsFinite(ppm))
        {
            return;
        }

        ppm = Math.Clamp(ppm, -1000.0, 1000.0);
        double previous = Volatile.Read(ref _outputPpm);
        Volatile.Write(ref _outputPpm, previous == 0 ? ppm : previous * 0.9 + ppm * 0.1);
    }

    public static void Reset() => Volatile.Write(ref _outputPpm, 0);
}
