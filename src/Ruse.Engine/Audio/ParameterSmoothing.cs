namespace Ruse.App.Audio;

/// <summary>Short linear glide used by effects whose parameters are edited live.</summary>
internal static class ParameterSmoothing
{
    private const float Samples = 480f; // 10 ms at 48 kHz
    private const float SlowSamples = 4800f; // 100 ms at 48 kHz

    /// <summary>Per-sample coefficient for a 10 ms one-pole glide.</summary>
    public const float FastAlpha = 0.002081f;

    /// <summary>Per-sample coefficient for a 100 ms one-pole glide.</summary>
    public const float SlowAlpha = 0.0002081f;

    public static float Move(float current, float target, int frames)
    {
        return Move(current, target, frames, Samples);
    }

    public static float MoveSlow(float current, float target, int frames)
    {
        return Move(current, target, frames, SlowSamples);
    }

    public static float Approach(float current, float target, float alpha)
    {
        return current + ((target - current) * alpha);
    }

    private static float Move(float current, float target, int frames, float samples)
    {
        float delta = target - current;
        if (delta == 0f)
        {
            return current;
        }

        float step = delta * Math.Min(1f, frames / samples);
        if (Math.Abs(step) < 1e-6f)
        {
            return target;
        }

        return current + step;
    }
}
