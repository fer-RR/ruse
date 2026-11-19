using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>
/// Current Silero VAD ONNX wrapper. The model consumes 512 new samples plus the
/// 64-sample context from the previous window. One instance owns one recurrent
/// state and is intended to be used by a single segmentation worker.
/// </summary>
internal sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16000;
    public const int WindowSamples = 512;
    private const int ContextSamples = 64;
    private const int InputSamples = WindowSamples + ContextSamples;
    private const int StateSize = 2 * 1 * 128;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[InputSamples];
    private readonly float[] _context = new float[ContextSamples];
    private readonly float[] _state = new float[StateSize];
    private readonly long[] _rate = { SampleRate };
    private readonly DenseTensor<long> _rateTensor;
    private readonly DenseTensor<float> _inputTensor;
    private readonly DenseTensor<float> _stateTensor;
    private readonly NamedOnnxValue _inputValue;
    private readonly NamedOnnxValue _stateValue;
    private readonly NamedOnnxValue _rateValue;
    private readonly NamedOnnxValue[] _inputs;
    private int _disposed;

    private SileroVad(string path)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
        };
        _session = new InferenceSession(path, options);
        _rateTensor = new DenseTensor<long>(_rate, new[] { 1 });
        _inputTensor = new DenseTensor<float>(_input, new[] { 1, InputSamples });
        _stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        _inputValue = NamedOnnxValue.CreateFromTensor("input", _inputTensor);
        _stateValue = NamedOnnxValue.CreateFromTensor("state", _stateTensor);
        _rateValue = NamedOnnxValue.CreateFromTensor("sr", _rateTensor);
        _inputs = new[] { _inputValue, _stateValue, _rateValue };
    }

    public static SileroVad? TryCreate()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        if (!File.Exists(path))
        {
            AppLog.Warning("Silero VAD model was not found; falling back to energy VAD.");
            return null;
        }

        try
        {
            return new SileroVad(path);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not load Silero VAD; falling back to energy VAD. " + ex.GetBaseException().Message);
            return null;
        }
    }

    public float Process(ReadOnlySpan<float> samples)
    {
        if (samples.Length < WindowSamples) return 0f;
        _context.CopyTo(_input, 0);
        samples[..WindowSamples].CopyTo(_input.AsSpan(ContextSamples));

        using var result = _session.Run(_inputs);

        float probability = 0f;
        foreach (var value in result)
        {
            if (value.Name == "output")
            {
                var output = value.AsTensor<float>();
                probability = output.Length > 0 ? output.GetValue(0) : 0f;
            }
            else if (value.Name == "stateN")
            {
                var next = value.AsTensor<float>();
                int count = (int)Math.Min(_state.Length, next.Length);
                for (int i = 0; i < count; i++)
                {
                    _state[i] = next.GetValue(i);
                }
            }
        }

        _input.AsSpan(InputSamples - ContextSamples).CopyTo(_context);
        return float.IsFinite(probability) ? Math.Clamp(probability, 0f, 1f) : 0f;
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Dispose();
    }
}
