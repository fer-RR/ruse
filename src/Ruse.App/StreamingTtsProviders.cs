using System.IO;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using PiperSharp;
using PiperSharp.Models;

namespace Ruse.App;

internal interface IStreamingTtsProvider : IAsyncDisposable
{
    Task StreamAsync(string text, float speed, int voice, Action<float[], int> onSamples, CancellationToken token);
}

internal static class StreamingTtsProviders
{
    public static IStreamingTtsProvider Create(string modelId) =>
        TtsModels.Find(modelId)?.Family == "piper"
            ? new PiperStreamingProvider()
            : new KokoroStreamingProvider();

    public static bool IsInstalled(string modelId) =>
        TtsModels.Find(modelId)?.Family == "piper"
            ? PiperStreamingProvider.IsInstalled
            : KokoroStreamingProvider.IsInstalled;

    public static Task DownloadAsync(string modelId, IProgress<TtsModelProgress>? progress, CancellationToken token) =>
        TtsModels.Find(modelId)?.Family == "piper"
            ? PiperStreamingProvider.DownloadAsync(progress, token)
            : KokoroStreamingProvider.DownloadAsync(progress, token);

    public static void Delete(string modelId)
    {
        if (TtsModels.Find(modelId)?.Family == "piper") PiperStreamingProvider.Delete();
        else KokoroStreamingProvider.Delete();
    }

    public static long InstalledSize(string modelId) =>
        TtsModels.Find(modelId)?.Family == "piper"
            ? PiperStreamingProvider.InstalledSize
            : KokoroStreamingProvider.InstalledSize;
}

internal sealed class KokoroStreamingProvider : IStreamingTtsProvider
{
    private const int SampleRate = 24000;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static KokoroWavSynthesizer? _synthesizer;

    public static string Root => Path.Combine(AppSettings.Directory, "models", "kokoro-streaming");
    public static string ModelPath => Path.Combine(Root, "kokoro.onnx");
    public static bool IsInstalled => File.Exists(ModelPath);

    public static long InstalledSize
    {
        get
        {
            try { return File.Exists(ModelPath) ? new FileInfo(ModelPath).Length : 0; }
            catch { return 0; }
        }
    }

    public static async Task DownloadAsync(IProgress<TtsModelProgress>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsInstalled) return;
            Directory.CreateDirectory(Root);
            string downloaded = await KokoroLoader.DownloadModelAsync(KModel.float32, value =>
                progress?.Report(new TtsModelProgress("kokoro-streaming", "downloading", value, "")))
                .ConfigureAwait(false);
            string source = Path.GetFullPath(downloaded);
            if (!string.Equals(source, ModelPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(source, ModelPath, overwrite: true);
            }
            progress?.Report(new TtsModelProgress("kokoro-streaming", "done", 1, ""));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Delete()
    {
        _synthesizer = null;
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }

    private static async Task<KokoroWavSynthesizer> EnsureAsync(CancellationToken token)
    {
        if (_synthesizer != null) return _synthesizer;
        if (!IsInstalled)
        {
            await DownloadAsync(null, token).ConfigureAwait(false);
        }
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_synthesizer == null)
            {
                var options = new SessionOptions
                {
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                _synthesizer = KokoroWavSynthesizer.LoadModel(ModelPath, options);
            }
            return _synthesizer;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task StreamAsync(string text, float speed, int voice, Action<float[], int> onSamples, CancellationToken token)
    {
        var synthesizer = await EnsureAsync(token).ConfigureAwait(false);
        string[] names = { "af_heart", "af_bella", "am_michael" };
        KokoroVoice? selected = voice >= 0 && voice < names.Length ? KokoroVoiceManager.GetVoice(names[voice]) : null;
        if (selected == null)
        {
            var voices = KokoroVoiceManager.Voices;
            if (voices.Count > 0) selected = voices[Math.Clamp(voice, 0, voices.Count - 1)];
        }
        if (selected == null) throw new InvalidOperationException("Kokoro has no voices installed.");

        var converter = new MonoToStereo48k(SampleRate, onSamples);
        var config = new KokoroTTSPipelineConfig { Speed = Math.Clamp(speed, 0.5f, 2f), PreprocessText = true };
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        synthesizer.Synthesize(text, selected, samples => converter.Push(samples), () => done.TrySetResult(true), config);
        await done.Task.WaitAsync(token).ConfigureAwait(false);
        converter.Flush();
    }

    private static void PushPcm16(byte[] buffer, int length, MonoToStereo48k converter)
    {
        int count = Math.Max(0, length / 2);
        var samples = new float[count];
        for (int index = 0; index < count; index++)
        {
            samples[index] = (short)(buffer[index * 2] | (buffer[index * 2 + 1] << 8)) / 32768f;
        }
        converter.Push(samples);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
internal sealed class PiperStreamingProvider : IStreamingTtsProvider
{
    private const string VoiceKey = "en_US-lessac-medium";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _piperDirectory;
    private static VoiceModel? _voice;

    public static string Root => Path.Combine(AppSettings.Directory, "models", "piper");
    public static bool IsInstalled => File.Exists(Path.Combine(Root, ".installed"));

    public static long InstalledSize
    {
        get
        {
            if (!Directory.Exists(Root)) return 0;
            try
            {
                return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        }
    }

    public static async Task DownloadAsync(IProgress<TtsModelProgress>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsInstalled) return;
            Directory.CreateDirectory(Root);
            progress?.Report(new TtsModelProgress("piper-en-us", "downloading", 0, ""));
            await PiperDownloader.DownloadPiper().ExtractPiper(Root).ConfigureAwait(false);
            var model = await PiperDownloader.DownloadModelByKey(VoiceKey).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Piper voice could not be downloaded.");
            _voice = model;
            File.WriteAllText(Path.Combine(Root, ".installed"), "1");
            progress?.Report(new TtsModelProgress("piper-en-us", "done", 100, ""));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Delete()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        _piperDirectory = null;
        _voice = null;
    }

    private static async Task<(string Directory, VoiceModel Voice)> EnsureAsync(CancellationToken token)
    {
        if (!IsInstalled)
        {
            await DownloadAsync(null, token).ConfigureAwait(false);
        }
        if (_voice == null)
        {
            _voice = await PiperDownloader.DownloadModelByKey(VoiceKey).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Piper voice is not installed.");
        }
        _piperDirectory ??= Directory.EnumerateDirectories(Root).FirstOrDefault(path => File.Exists(Path.Combine(path, "piper.exe")))
            ?? Root;
        return (_piperDirectory, _voice);
    }

    public async Task StreamAsync(string text, float speed, int voice, Action<float[], int> onSamples, CancellationToken token)
    {
        var (directory, model) = await EnsureAsync(token).ConfigureAwait(false);
        string executable = Path.Combine(directory, "piper.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The Piper executable is missing.", executable);
        var wave = new PiperWaveProvider(new PiperConfiguration
        {
            ExecutableLocation = executable,
            WorkingDirectory = directory,
            Model = model,
            SpeakingRate = Math.Clamp(speed, 0.5f, 2f),
        });
        wave.Start();
        var converter = new MonoToStereo48k(wave.WaveFormat.SampleRate, onSamples);
        var infer = wave.InferPlayback(text, token);
        var wait = wave.WaitForExit(token);
        var buffer = new byte[4096];
        while (!wait.IsCompleted)
        {
            int read = wave.Read(buffer, 0, buffer.Length);
            if (read > 0) PushPcm16(buffer, read, converter);
            else await Task.Delay(5, token).ConfigureAwait(false);
        }

        int tail;
        while ((tail = wave.Read(buffer, 0, buffer.Length)) > 0)
        {
            PushPcm16(buffer, tail, converter);
        }

        await infer.ConfigureAwait(false);
        await wait.ConfigureAwait(false);
        converter.Flush();
    }

    private static void PushPcm16(byte[] buffer, int length, MonoToStereo48k converter)
    {
        int count = Math.Max(0, length / 2);
        var samples = new float[count];
        for (int index = 0; index < count; index++)
        {
            samples[index] = (short)(buffer[index * 2] | (buffer[index * 2 + 1] << 8)) / 32768f;
        }
        converter.Push(samples);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class MonoToStereo48k
{
    private readonly double _step;
    private readonly Action<float[], int> _emit;
    private readonly List<float> _samples = new();
    private double _position;

    public MonoToStereo48k(int sourceRate, Action<float[], int> emit)
    {
        _step = sourceRate / 48000.0;
        _emit = emit;
    }

    public void Push(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return;
        _samples.AddRange(input.ToArray());
        int frames = (int)Math.Floor((_samples.Count - 1 - _position) / _step) + 1;
        if (frames <= 0) return;
        var output = new float[frames * 2];
        int emitted = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = (int)_position;
            if (index + 1 >= _samples.Count) break;
            float fraction = (float)(_position - index);
            float sample = _samples[index] + (_samples[index + 1] - _samples[index]) * fraction;
            output[frame * 2] = sample;
            output[frame * 2 + 1] = sample;
            emitted++;
            _position += _step;
        }
        if (emitted == 0) return;
        int consumed = (int)_position;
        if (consumed > 0)
        {
            _samples.RemoveRange(0, consumed);
            _position -= consumed;
        }
        if (emitted < frames) Array.Resize(ref output, emitted * 2);
        _emit(output, emitted);
    }

    public void Flush()
    {
        if (_samples.Count > 0)
        {
            var output = new float[_samples.Count * 2];
            for (int index = 0; index < _samples.Count; index++)
            {
                output[index * 2] = _samples[index];
                output[index * 2 + 1] = _samples[index];
            }
            _emit(output, _samples.Count);
            _samples.Clear();
            _position = 0;
        }
    }
}
