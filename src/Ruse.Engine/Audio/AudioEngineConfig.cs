namespace Ruse.App.Audio;

/// <summary>
/// Global audio-engine timing choices. Values are read when a graph/output is
/// started; changing them while routing is active requires a restart.
/// </summary>
public static class AudioEngineConfig
{
    public const int SampleRate = 48000;
    public const int MinBlockFrames = 120;
    public const int MaxBlockFrames = 480;

    private static int _blockFrames = 240;
    private static int _engineCushionFrames = 480;
    private static int _outputBufferPeriods = 2;
    private static int _outputBufferManualMs;
    private static int _elasticRing;
    private static int _version;

    public static bool ElasticRing
    {
        get => Volatile.Read(ref _elasticRing) != 0;
        set
        {
            if (value == ElasticRing) return;
            Volatile.Write(ref _elasticRing, value ? 1 : 0);
            Interlocked.Increment(ref _version);
        }
    }

    public static int Version => Volatile.Read(ref _version);

    public static int BlockFrames
    {
        get => Volatile.Read(ref _blockFrames);
        set => Volatile.Write(ref _blockFrames,
            Math.Clamp(value, MinBlockFrames, MaxBlockFrames));
    }

    public static int EngineCushionFrames
    {
        get => Volatile.Read(ref _engineCushionFrames);
        set => Volatile.Write(ref _engineCushionFrames,
            Math.Max(BlockFrames, value));
    }

    public static int OutputBufferPeriods
    {
        get => Volatile.Read(ref _outputBufferPeriods);
        set => Volatile.Write(ref _outputBufferPeriods, Math.Clamp(value, 1, 3));
    }

    public static int OutputBufferManualMs
    {
        get => Volatile.Read(ref _outputBufferManualMs);
        set => Volatile.Write(ref _outputBufferManualMs, Math.Clamp(value, 0, 40));
    }

    public static int FramesForMs(double milliseconds) =>
        Math.Clamp((int)Math.Round(milliseconds * SampleRate / 1000.0),
            MinBlockFrames, MaxBlockFrames);

    public static int CushionFramesForMs(double milliseconds) =>
        Math.Clamp((int)Math.Round(milliseconds * SampleRate / 1000.0),
            MinBlockFrames, 2400);

    public static void Configure(
        int blockFrames,
        int engineCushionFrames,
        int outputBufferPeriods,
        int outputBufferManualMs)
    {
        BlockFrames = blockFrames;
        EngineCushionFrames = engineCushionFrames;
        OutputBufferPeriods = outputBufferPeriods;
        OutputBufferManualMs = outputBufferManualMs;
        Interlocked.Increment(ref _version);
    }
}
