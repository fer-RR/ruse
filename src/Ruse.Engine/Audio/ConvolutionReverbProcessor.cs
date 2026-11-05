using NAudio.Effects;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Convolution reverb with a built-in synthetic room and support for loading
/// real impulse responses. The effect instance is swapped atomically when an IR
/// changes, so the render thread never blocks on file I/O.
/// </summary>
public sealed class ConvolutionReverbProcessor : IAudioProcessor, IParameterizedProcessor, IImpulseResponseProcessor
{
    public const string Id = "convolution-reverb";

    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MaxIrSeconds = 12;

    private readonly AudioParameter[] _parameters;
    private volatile float _mix = 0.35f;

    private ConvolutionReverbEffect _effect;
    private string? _path;
    private string _name = "Default room";
    private string? _error;

    public ConvolutionReverbProcessor()
    {
        _effect = CreateEffect(CreateDefaultImpulseResponse(), null);
        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 1f,
                Default = 0.35f,
                Get = () => _mix,
                Set = value => _mix = Math.Clamp(value, 0f, 1f),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Convolution reverb";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => Volatile.Read(ref _effect).LatencySamples;

    public string? ImpulseResponsePath => _path;
    public string ImpulseResponseName => _name;
    public string? ImpulseResponseError => _error;
    public bool HasCustomImpulseResponse => _path != null;

    public void Process(Span<float> samples, int frames)
    {
        var effect = Volatile.Read(ref _effect);
        effect.Mix = _mix;
        effect.Process(samples);
    }

    public void Reset() => Volatile.Read(ref _effect).Reset();

    public void LoadImpulseResponse(string path)
    {
        try
        {
            var (mono, stereo) = ReadImpulseResponse(path);
            var effect = CreateEffect(mono, stereo);
            Volatile.Write(ref _effect, effect);
            _path = path;
            _name = Path.GetFileNameWithoutExtension(path);
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            AppLog.Warning($"Could not load impulse response '{path}': {ex.Message}");
            throw;
        }
    }

    public void ClearImpulseResponse()
    {
        Volatile.Write(ref _effect, CreateEffect(CreateDefaultImpulseResponse(), null));
        _path = null;
        _name = "Default room";
        _error = null;
    }

    private static ConvolutionReverbEffect CreateEffect(float[]? mono, float[][]? stereo)
    {
        var effect = new ConvolutionReverbEffect
        {
            PartitionSize = 256,
        };
        effect.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
        if (stereo != null)
        {
            effect.SetImpulseResponse(stereo);
        }
        else if (mono != null)
        {
            effect.SetImpulseResponse(mono);
        }

        return effect;
    }

    private static readonly Lazy<float[]> DefaultImpulse =
        new(CreateDefaultImpulseResponseCore);

    private static float[] CreateDefaultImpulseResponse() =>
        (float[])DefaultImpulse.Value.Clone();

    private static float[] CreateDefaultImpulseResponseCore()
    {
        int frames = SampleRate * 2;
        var impulse = new float[frames];
        var random = new Random(0x52555345);

        // A short pre-delay, a few early reflections and a smooth noisy tail.
        int preDelay = SampleRate / 50; // 20 ms
        for (int i = preDelay; i < frames; i++)
        {
            float t = (i - preDelay) / (float)(frames - preDelay);
            float envelope = MathF.Pow(1f - t, 2.6f) * MathF.Exp(-t * 1.4f);
            float noise = (random.NextSingle() * 2f) - 1f;
            impulse[i] = noise * envelope * 0.28f;
        }

        int[] reflections = { SampleRate / 100, SampleRate / 45, SampleRate / 28 };
        float[] gains = { 0.45f, 0.3f, 0.18f };
        for (int r = 0; r < reflections.Length; r++)
        {
            int index = preDelay + reflections[r];
            if (index < frames)
            {
                impulse[index] += gains[r];
            }
        }

        // Keep the synthetic room at unit energy so the default reverb does not
        // jump in level or clip when it is added to a route.
        double energy = 0;
        for (int i = 0; i < impulse.Length; i++)
        {
            energy += impulse[i] * impulse[i];
        }

        if (energy > 1e-12)
        {
            float scale = (float)(1.0 / Math.Sqrt(energy));
            for (int i = 0; i < impulse.Length; i++)
            {
                impulse[i] *= scale;
            }
        }

        return impulse;
    }

    private static (float[]? Mono, float[][]? Stereo) ReadImpulseResponse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The impulse response file was not found.", path);
        }

        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (reader.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(reader, SampleRate);
        }

        int sourceChannels = Math.Max(1, provider.WaveFormat.Channels);
        int maxSamples = SampleRate * MaxIrSeconds * sourceChannels;
        var samples = new List<float>(Math.Min(maxSamples, 1 << 18));
        var buffer = new float[8192];
        while (samples.Count < maxSamples)
        {
            int want = Math.Min(buffer.Length, maxSamples - samples.Count);
            int read = provider.Read(buffer.AsSpan(0, want));
            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        if (samples.Count == 0)
        {
            throw new InvalidDataException("The impulse response file contains no audio.");
        }

        int frames = samples.Count / sourceChannels;
        if (sourceChannels == 1)
        {
            var mono = new float[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                mono[frame] = samples[frame];
            }

            NormalizeImpulseResponse(mono);
            return (mono, null);
        }

        var left = new float[frames];
        var right = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            left[frame] = samples[frame * sourceChannels];
            right[frame] = samples[(frame * sourceChannels) + 1];
        }

        NormalizeImpulseResponse(left, right);
        return (null, new[] { left, right });
    }

    private static void NormalizeImpulseResponse(float[] mono)
    {
        double energy = 0;
        for (int i = 0; i < mono.Length; i++)
        {
            energy += mono[i] * mono[i];
        }

        if (energy <= 1e-12)
        {
            return;
        }

        float scale = (float)(1.0 / Math.Sqrt(energy));
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] *= scale;
        }
    }

    private static void NormalizeImpulseResponse(float[] left, float[] right)
    {
        double energy = 0;
        for (int i = 0; i < left.Length; i++)
        {
            energy += left[i] * left[i] + right[i] * right[i];
        }

        if (energy <= 1e-12)
        {
            return;
        }

        float scale = (float)(1.0 / Math.Sqrt(energy));
        for (int i = 0; i < left.Length; i++)
        {
            left[i] *= scale;
            right[i] *= scale;
        }
    }
}
