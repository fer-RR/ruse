namespace Ruse.App.Audio;

/// <summary>Passes 48 kHz stereo audio to the shared 16 kHz mono speech tap.</summary>
public sealed class TranscriptionProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "transcriber";
    public const string TapParameterName = "__tap";

    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);
    private volatile float _tapId;

    public TranscriptionProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = TapParameterName,
                Min = 0,
                Max = 16_000_000,
                IsInteger = true,
                Get = () => _tapId,
                Set = value => _tapId = Math.Clamp(MathF.Round(value), 0, 16_000_000),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Transcriber";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);

    public void SetTapId(int id)
    {
        _tapId = id;
        EnsureTap(id);
    }

    public void Process(Span<float> samples, int frames)
    {
        WriteTap(TapId, samples, frames);
    }

    public void Reset()
    {
        if (SpeechTapRegistry.TryGet(TapId, out var tap)) tap.Clear();
    }

    public static void EnsureTap(int id)
    {
        if (id > 0) SpeechTapRegistry.GetOrAdd(id);
    }

    /// <summary>Converts and appends one mixed block. Safe to call from any audio thread.</summary>
    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames)
    {
        if (SpeechTapRegistry.TryGet(id, out var tap)) tap.Write(samples, frames);
    }

    public static int ReadAvailable(int id, byte[] destination) =>
        SpeechTapRegistry.TryGet(id, out var tap) ? tap.ReadBytes(destination) : 0;

    /// <summary>Most recent peak after the anti-alias filter, for the node meter.</summary>
    public static float GetLevel(int id)
    {
        if (!SpeechTapRegistry.TryGet(id, out var tap))
        {
            return 0f;
        }

        // Fade a stalled meter instead of freezing the last peak.
        long age = Environment.TickCount64 - tap.LastWriteTick;
        float level = tap.Level;
        if (age > 120)
        {
            level *= MathF.Max(0f, 1f - ((age - 120) / 1200f));
        }

        return level;
    }

    /// <summary>Tick count of the last block that carried audible signal, or 0.</summary>
    public static long GetLastAudioTick(int id) =>
        SpeechTapRegistry.TryGet(id, out var tap) ? tap.LastAudioTick : 0;

    public static void ClearTap(int id)
    {
        if (SpeechTapRegistry.TryGet(id, out var tap)) tap.Clear();
    }

    public static void RemoveTap(int id) => SpeechTapRegistry.Remove(id);

    public static int NextTapId()
    {
        while (true)
        {
            int id = Interlocked.Increment(ref _nextTapId);
            if (!SpeechTapRegistry.TryGet(id, out _)) return id;
        }
    }
}
