namespace Ruse.App.Audio;

/// <summary>
/// Passes 48 kHz stereo audio to a 16 kHz mono tap for the offline translator.
/// It shares the tap buffer implementation with the transcriber.
/// </summary>
public sealed class TranslatorProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "translator";

    private volatile float _tapId;

    public TranslatorProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = TranscriptionProcessor.TapParameterName,
                Min = 0,
                Max = 16_000_000,
                IsInteger = true,
                Get = () => _tapId,
                Set = value => _tapId = Math.Clamp(MathF.Round(value), 0, 16_000_000),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Translator";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);

    public void SetTapId(int id)
    {
        _tapId = id;
        TranscriptionProcessor.EnsureTap(id);
    }

    public void Process(Span<float> samples, int frames)
    {
        TranscriptionProcessor.WriteTap(TapId, samples, frames);
    }

    public void Reset()
    {
        TranscriptionProcessor.ClearTap(TapId);
    }
}
