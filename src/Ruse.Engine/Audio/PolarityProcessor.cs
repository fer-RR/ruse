namespace Ruse.App.Audio;

/// <summary>
/// Inverts the phase of one or both channels. Useful when combining a
/// microphone with a loopback or a second mic that is wired out of phase.
/// </summary>
public sealed class PolarityProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "polarity";

    private volatile bool _invertLeft;
    private volatile bool _invertRight;

    public PolarityProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Invert Left",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsToggle = true,
                Get = () => _invertLeft ? 1f : 0f,
                Set = value => _invertLeft = value >= 0.5f,
            },
            new AudioParameter
            {
                Name = "Invert Right",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsToggle = true,
                Get = () => _invertRight ? 1f : 0f,
                Set = value => _invertRight = value >= 0.5f,
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Polarity";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        bool left = _invertLeft;
        bool right = _invertRight;
        if (!left && !right)
        {
            return;
        }

        int channels = frames > 0 ? Math.Max(1, samples.Length / frames) : 1;
        if (channels < 2)
        {
            if (left || right)
            {
                for (int i = 0; i < samples.Length; i++) samples[i] = -samples[i];
            }

            return;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            if (left) samples[index] = -samples[index];
            if (right) samples[index + 1] = -samples[index + 1];
        }
    }

    public void Reset()
    {
    }
}
