using System.IO;
using System.IO.Compression;
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
    private static readonly object Sync = new();
    private static KokoroWavSynthesizer? _synthesizer;
    private static bool _verified;
    private static int _active;
    private static bool _deletePending;

    private const string ModelSha256 = "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05";

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

            ModelIntegrity.Verify(
                await ModelIntegrity.ComputeFileSha256Async(ModelPath, token).ConfigureAwait(false),
                ModelSha256, "Kokoro model");
            _verified = true;
            progress?.Report(new TtsModelProgress("kokoro-streaming", "done", 1, ""));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Delete()
    {
        KokoroWavSynthesizer? old = null;
        lock (Sync)
        {
            _deletePending = true;
            if (_active == 0)
            {
                old = _synthesizer;
                _synthesizer = null;
                _deletePending = false;
            }
        }

        (old as IDisposable)?.Dispose();
        _verified = false;
        DeleteModelFiles();
    }

    private static void DeleteModelFiles()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch
        {
        }
    }

    private static void ReleaseIfIdle()
    {
        KokoroWavSynthesizer? old = null;
        bool deleteFiles = false;
        lock (Sync)
        {
            if (_active == 0 && _deletePending)
            {
                old = _synthesizer;
                _synthesizer = null;
                _deletePending = false;
                deleteFiles = true;
            }
        }

        (old as IDisposable)?.Dispose();
        if (deleteFiles)
        {
            DeleteModelFiles();
        }
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
                if (!_verified)
                {
                    ModelIntegrity.Verify(
                        await ModelIntegrity.ComputeFileSha256Async(ModelPath, token).ConfigureAwait(false),
                        ModelSha256, "Kokoro model");
                    _verified = true;
                }

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
        lock (Sync)
        {
            if (_deletePending)
            {
                throw new OperationCanceledException("The Kokoro model is being deleted.");
            }

            _active++;
        }

        try
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
            bool canceled = false;
            synthesizer.Synthesize(text, selected, samples =>
            {
                if (canceled || token.IsCancellationRequested)
                {
                    canceled = true;
                    return;
                }

                converter.Push(samples);
            }, () => done.TrySetResult(true), config);
            try
            {
                await done.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                throw;
            }

            if (!canceled && !token.IsCancellationRequested)
            {
                converter.Flush();
            }
        }
        finally
        {
            lock (Sync)
            {
                _active--;
            }

            ReleaseIfIdle();
        }
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
    private const string PiperVersion = "2023.11.14-2";
    private const string PiperZipSha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea";
    private const string VoiceOnnxSha256 = "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f";
    private const string VoiceJsonSha256 = "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ActiveSync = new();
    private static readonly HashSet<PiperWaveProvider> Active = new();
    private static readonly System.Reflection.FieldInfo? ProcessField =
        typeof(PiperWaveProvider).GetField("_process",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static string? _piperDirectory;
    private static VoiceModel? _voice;
    private static bool _verified;
    private static bool _deleting;

    private static void Release(PiperWaveProvider wave)
    {
        try
        {
            if (ProcessField?.GetValue(wave) is System.Diagnostics.Process process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                process.Dispose();
            }
        }
        catch
        {
        }
    }

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
            string archive = Path.Combine(Root, "piper.zip");
            using (var input = await PiperDownloader.DownloadPiper(PiperVersion).ConfigureAwait(false))
            {
                await using var output = File.Create(archive);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }

            ModelIntegrity.Verify(
                await ModelIntegrity.ComputeFileSha256Async(archive, token).ConfigureAwait(false),
                PiperZipSha256, "Piper executable");
            ZipFile.ExtractToDirectory(archive, Root, overwriteFiles: true);
            try { File.Delete(archive); } catch { }

            var model = await PiperDownloader.GetModelByKey(VoiceKey).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Piper voice could not be downloaded.");
            await model.DownloadModel(Root).ConfigureAwait(false);
            VerifyVoice(model);
            _voice = model;
            _verified = true;
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
        PiperWaveProvider[] active;
        lock (ActiveSync)
        {
            _deleting = true;
            active = Active.ToArray();
            Active.Clear();
        }

        foreach (var wave in active)
        {
            Release(wave);
        }

        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        _piperDirectory = null;
        _voice = null;
        _verified = false;
        lock (ActiveSync)
        {
            _deleting = false;
        }
    }

    private static async Task<(string Directory, VoiceModel Voice)> EnsureAsync(CancellationToken token)
    {
        if (!IsInstalled)
        {
            await DownloadAsync(null, token).ConfigureAwait(false);
        }
        if (_voice == null)
        {
            _voice = await PiperDownloader.GetModelByKey(VoiceKey).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Piper voice is not installed.");
            _voice.ModelLocation = Path.Combine(Root, VoiceKey);
        }

        if (!_verified)
        {
            VerifyVoice(_voice);
            _verified = true;
        }

        _piperDirectory ??= Directory.EnumerateDirectories(Root).FirstOrDefault(path => File.Exists(Path.Combine(path, "piper.exe")))
            ?? Root;
        return (_piperDirectory, _voice);
    }

    private static void VerifyVoice(VoiceModel model)
    {
        string directory = model.ModelLocation ?? Path.Combine(Root, VoiceKey);
        ModelIntegrity.Verify(
            ModelIntegrity.ComputeFileSha256Async(Path.Combine(directory, VoiceKey + ".onnx")).GetAwaiter().GetResult(),
            VoiceOnnxSha256, "Piper voice");
        ModelIntegrity.Verify(
            ModelIntegrity.ComputeFileSha256Async(Path.Combine(directory, VoiceKey + ".onnx.json")).GetAwaiter().GetResult(),
            VoiceJsonSha256, "Piper voice configuration");
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
        lock (ActiveSync)
        {
            if (_deleting)
            {
                Release(wave);
                throw new OperationCanceledException("The Piper model is being deleted.");
            }

            Active.Add(wave);
        }

        try
        {
            wave.Start();
            var converter = new MonoToStereo48k(wave.WaveFormat.SampleRate, onSamples);
            var infer = wave.InferPlayback(text, token);
            var wait = wave.WaitForExit(token);
            var buffer = new byte[4096];
            while (!wait.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                int read = wave.Read(buffer, 0, buffer.Length);
                if (read > 0) PushPcm16(buffer, read, converter);
                else await Task.Delay(5, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            int tail;
            while ((tail = wave.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                PushPcm16(buffer, tail, converter);
            }

            await infer.ConfigureAwait(false);
            await wait.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            converter.Flush();
        }
        finally
        {
            lock (ActiveSync)
            {
                Active.Remove(wave);
            }

            Release(wave);
        }
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
