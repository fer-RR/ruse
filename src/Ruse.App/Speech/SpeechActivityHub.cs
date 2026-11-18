namespace Ruse.App;

/// <summary>Process-wide speech activity for barge-in and live indicators.</summary>
internal static class SpeechActivityHub
{
    public static event Action<int>? SpeechStarted;
    public static event Action<int>? SpeechEnded;

    public static void PublishSpeechStarted(int tapId) => SpeechStarted?.Invoke(tapId);

    public static void PublishSpeechEnded(int tapId) => SpeechEnded?.Invoke(tapId);
}
