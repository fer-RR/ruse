using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using Whisper.net;

namespace Ruse.App;

internal sealed record TranslationSnapshot(
    string Status,
    string SourceText,
    string SourcePartial,
    string TranslatedText,
    string TranslatedPartial,
    string? Error,
    float Level,
    bool HasAudio,
    double ElapsedSeconds,
    string? DetectedLanguage);

internal sealed class TranslationSession : IDisposable
{
    private const int SampleRate = 16000;
    private const int MinChunkSamples = SampleRate * 2;
    private const int MaxChunkSamples = SampleRate * 10;
    private const int TranslationContextSize = 2048;

    private static readonly SemaphoreSlim TranslationGate = new(1, 1);

    private readonly int _tapId;
    private readonly string _speechModelId;
    private readonly string _translationModelId;
    private readonly string _targetCode;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _textLock = new();
    private readonly object _sampleLock = new();
    private readonly List<float> _samples = new(MaxChunkSamples + 8192);
    private readonly Task _worker;
    private readonly long _startedAt = Environment.TickCount64;

    private StatelessExecutor? _translator;
    private string _baseStatus = "Starting";
    private string _sourceText = "";
    private string _sourcePartial = "";
    private string _translatedText = "";
    private string _translatedPartial = "";
    private string? _error;
    private string? _detectedLanguage;
    private bool _modelReady;
    private bool _processing;
    private int _disposed;

    public TranslationSession(int tapId, string speechModelId, string translationModelId, string targetCode)
    {
        _tapId = tapId;
        _speechModelId = speechModelId;
        _translationModelId = translationModelId;
        _targetCode = TranslationLanguages.Normalize(targetCode);
        _worker = Task.Run(RunAsync);
    }

    public string SpeechModelId => _speechModelId;
    public string TranslationModelId => _translationModelId;
    public string TargetCode => _targetCode;

    public TranslationSnapshot Snapshot()
    {
        long lastAudio = TranscriptionProcessor.GetLastAudioTick(_tapId);
        bool hasAudio = lastAudio > 0 && Environment.TickCount64 - lastAudio < 2500;
        float level = TranscriptionProcessor.GetLevel(_tapId);

        lock (_textLock)
        {
            string status = !_modelReady
                ? _baseStatus
                : _processing ? _baseStatus : "Listening";
            return new TranslationSnapshot(
                status,
                _sourceText,
                _sourcePartial,
                _translatedText,
                _translatedPartial,
                _error,
                level,
                hasAudio,
                Math.Max(0, (Environment.TickCount64 - _startedAt) / 1000.0),
                _detectedLanguage);
        }
    }

    public void Clear()
    {
        TranscriptionProcessor.ClearTap(_tapId);
        lock (_sampleLock) _samples.Clear();
        lock (_textLock)
        {
            _sourceText = "";
            _sourcePartial = "";
            _translatedText = "";
            _translatedPartial = "";
            _error = null;
            _detectedLanguage = null;
        }
    }

    private async Task RunAsync()
    {
        WhisperProcessor? processor = null;
        bool translationAcquired = false;
        try
        {
            string? speechPath = TranscriptionModels.GetInstalledPath(_speechModelId);
            if (speechPath == null)
            {
                SetStatus("No speech model installed");
                return;
            }

            if (!TranscriptionModels.IsMultilingual(_speechModelId))
            {
                lock (_textLock) _error = "The translator needs a multilingual Whisper model.";
                SetStatus("Transcription error");
                return;
            }

            SetStatus("Checking speech model");
            var speechProbe = await ModelProbe.EnsureAsync(_speechModelId, speechPath, _cancel.Token);
            if (!speechProbe.Success)
            {
                lock (_textLock) _error = speechProbe.Message;
                SetStatus("Transcription error");
                AppLog.Warning(speechProbe.Message);
                return;
            }

            SetStatus("Loading speech model");
            var speechModel = TranscriptionModels.Find(_speechModelId);
            var factory = SpeechModelCache.Acquire(_speechModelId, speechPath, _cancel.Token);
            if (factory == null)
            {
                lock (_textLock) _error = "Could not load the speech model.";
                SetStatus("Transcription error");
                return;
            }

            if (TranslationLanguages.NeedsModel(_targetCode))
            {
                if (string.IsNullOrEmpty(_translationModelId))
                {
                    lock (_textLock) _error = "No translation model installed.";
                    SetStatus("No translation model installed");
                    return;
                }

                string? translationPath = TranslationModels.GetInstalledPath(_translationModelId);
                if (translationPath == null)
                {
                    lock (_textLock) _error = "No translation model installed.";
                    SetStatus("No translation model installed");
                    return;
                }

                SetStatus("Checking translation model");
                var translationProbe = await TranslationModelProbe.EnsureAsync(
                    _translationModelId, translationPath, _cancel.Token);
                if (!translationProbe.Success)
                {
                    lock (_textLock) _error = translationProbe.Message;
                    SetStatus("Translation error");
                    AppLog.Warning(translationProbe.Message);
                    return;
                }

                SetStatus("Loading translation model");
                var weights = TranslationModelCache.Acquire(_translationModelId, translationPath, _cancel.Token);
                if (weights == null)
                {
                    lock (_textLock) _error = "Could not load the translation model.";
                    SetStatus("Translation error");
                    return;
                }

                translationAcquired = true;
                _translator = new StatelessExecutor(weights, new ModelParams(translationPath)
                {
                    ContextSize = TranslationContextSize,
                    BatchSize = 256,
                    GpuLayerCount = 0,
                    Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                })
                {
                    ApplyTemplate = true,
                    SystemMessage = TranslationPrompt.System(_targetCode),
                };
            }

            try
            {
                var builder = factory.CreateBuilder()
                    .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                    .WithNoContext();
                builder = _targetCode == TranslationLanguages.English
                    ? builder.WithTranslate()
                    : builder.WithLanguage("auto");

                processor = builder.Build();
                _modelReady = true;
                SetStatus("Listening");
                await RunRecognitionAsync(processor);
            }
            finally
            {
                processor?.Dispose();
                SpeechModelCache.Release(_speechModelId);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (_textLock) _error = ex.Message;
            SetStatus("Translation error");
            AppLog.Error("Offline translation failed.", ex);
        }
        finally
        {
            if (translationAcquired)
            {
                TranslationModelCache.Release(_translationModelId);
            }
        }
    }

    private async Task RunRecognitionAsync(WhisperProcessor processor)
    {
        var buffer = new byte[8192];
        while (!_cancel.IsCancellationRequested)
        {
            int read = TranscriptionProcessor.ReadAvailable(_tapId, buffer);
            float[]? chunk = null;
            if (read > 0)
            {
                lock (_sampleLock)
                {
                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        short value = (short)(buffer[i] | (buffer[i + 1] << 8));
                        _samples.Add(value / 32768f);
                    }

                    long now = Environment.TickCount64;
                    long lastAudio = TranscriptionProcessor.GetLastAudioTick(_tapId);
                    bool silent = lastAudio > 0 && now - lastAudio > 1200;
                    if (_samples.Count >= MaxChunkSamples ||
                        (silent && _samples.Count >= MinChunkSamples))
                    {
                        chunk = _samples.ToArray();
                        _samples.Clear();
                    }
                    else if (lastAudio == 0 || now - lastAudio > 3000)
                    {
                        _samples.Clear();
                    }
                }
            }

            if (chunk != null)
            {
                await TranslateChunkAsync(processor, chunk);
            }

            await Task.Delay(40, _cancel.Token);
        }
    }

    private async Task TranslateChunkAsync(WhisperProcessor processor, float[] chunk)
    {
        lock (_textLock)
        {
            _processing = true;
            _sourcePartial = "…";
            _baseStatus = "Transcribing";
        }

        var source = new StringBuilder();
        string? detected = null;
        try
        {
            await foreach (var segment in processor.ProcessAsync(chunk, _cancel.Token))
            {
                string value = segment.Text.Trim();
                if (value.Length == 0 || (value.StartsWith('[') && value.EndsWith(']')))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(segment.Language))
                {
                    detected ??= segment.Language;
                }

                if (source.Length > 0) source.Append(' ');
                source.Append(value);
            }
        }
        finally
        {
            lock (_textLock)
            {
                _sourcePartial = "";
            }
        }

        string text = source.ToString().Trim();
        if (text.Length == 0)
        {
            lock (_textLock)
            {
                _processing = false;
                _baseStatus = "Listening";
            }

            return;
        }

        lock (_textLock)
        {
            _sourceText = _sourceText.Length == 0 ? text : _sourceText + " " + text;
            if (!string.IsNullOrWhiteSpace(detected))
            {
                _detectedLanguage = detected;
            }
        }

        if (_targetCode == TranslationLanguages.English ||
            TranslationLanguages.MatchesSource(_targetCode, detected))
        {
            AppendTranslation(text);
        }
        else if (_translator != null)
        {
            lock (_textLock)
            {
                _baseStatus = "Translating";
                _translatedPartial = "…";
            }

            try
            {
                string translated = await TranslateTextAsync(_translator, text, _cancel.Token);
                AppendTranslation(translated);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_textLock)
                {
                    _error = "Translation failed: " + ex.Message;
                }

                AppLog.Error("Offline translation inference failed.", ex);
            }
            finally
            {
                lock (_textLock)
                {
                    _translatedPartial = "";
                }
            }
        }

        lock (_textLock)
        {
            _processing = false;
            _baseStatus = "Listening";
        }
    }

    private static async Task<string> TranslateTextAsync(
        StatelessExecutor translator,
        string text,
        CancellationToken token)
    {
        await TranslationGate.WaitAsync(token);
        try
        {
            var inference = new InferenceParams
            {
                MaxTokens = Math.Clamp(text.Length * 2 + 64, 64, 512),
                SamplingPipeline = new GreedySamplingPipeline(),
                AntiPrompts = new List<string> { "<|im_end|>", "<|endoftext|>", "<|eot_id|>" },
            };

            var result = new StringBuilder();
            await foreach (var piece in translator.InferAsync(text, inference, token))
            {
                result.Append(piece);
            }

            return CleanTranslation(result.ToString());
        }
        finally
        {
            TranslationGate.Release();
        }
    }

    private static string CleanTranslation(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;

        foreach (string prefix in new[] { "Translation:", "Translation :", "Tradução:", "Traduccion:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].TrimStart();
                break;
            }
        }

        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')))
        {
            text = text[1..^1];
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private void AppendTranslation(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        lock (_textLock)
        {
            _translatedText = _translatedText.Length == 0
                ? text
                : _translatedText + " " + text;
            _translatedPartial = "";
        }
    }

    private void SetStatus(string status)
    {
        lock (_textLock) _baseStatus = status;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();
        try
        {
            // A large model probe can outlive the UI removal; leave the token
            // alive rather than disposing it under the worker.
            if (_worker.Wait(TimeSpan.FromMilliseconds(3000)))
            {
                _cancel.Dispose();
            }
        }
        catch
        {
        }
    }
}

internal static class TranslationRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<int, TranslationSession> Sessions = new();

    public static void Ensure(
        int tapId,
        string speechModelId,
        string translationModelId,
        string targetCode)
    {
        if (tapId <= 0 || string.IsNullOrWhiteSpace(speechModelId))
        {
            return;
        }

        targetCode = TranslationLanguages.Normalize(targetCode);
        TranslationSession? previous = null;
        lock (Lock)
        {
            if (Sessions.TryGetValue(tapId, out var current))
            {
                if (string.Equals(current.SpeechModelId, speechModelId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.TranslationModelId, translationModelId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.TargetCode, targetCode, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                previous = current;
                Sessions.Remove(tapId);
                TranscriptionProcessor.ClearTap(tapId);
            }
            else
            {
                TranscriptionProcessor.ClearTap(tapId);
            }

            Sessions[tapId] = new TranslationSession(tapId, speechModelId, translationModelId, targetCode);
        }

        previous?.Dispose();
    }

    public static TranslationSnapshot? Snapshot(int tapId)
    {
        lock (Lock)
        {
            return Sessions.TryGetValue(tapId, out var session) ? session.Snapshot() : null;
        }
    }

    public static void Clear(int tapId)
    {
        lock (Lock)
        {
            if (Sessions.TryGetValue(tapId, out var session)) session.Clear();
        }

        TranscriptionProcessor.ClearTap(tapId);
    }

    public static void Restart(
        int tapId,
        string speechModelId,
        string translationModelId,
        string targetCode)
    {
        Stop(tapId);
        Ensure(tapId, speechModelId, translationModelId, targetCode);
    }

    public static void Stop(int tapId)
    {
        TranslationSession? session;
        lock (Lock)
        {
            if (!Sessions.Remove(tapId, out session)) return;
        }

        session.Dispose();
    }

    public static void StopAll()
    {
        List<TranslationSession> sessions;
        lock (Lock)
        {
            sessions = Sessions.Values.ToList();
            Sessions.Clear();
        }

        foreach (var session in sessions) session.Dispose();
    }

    public static void StopSpeechModel(string modelId)
    {
        List<TranslationSession> sessions;
        lock (Lock)
        {
            var matches = Sessions
                .Where(pair => string.Equals(pair.Value.SpeechModelId, modelId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sessions = matches.Select(pair => pair.Value).ToList();
            foreach (var pair in matches) Sessions.Remove(pair.Key);
        }

        foreach (var session in sessions) session.Dispose();
    }

    public static void StopTranslationModel(string modelId)
    {
        List<TranslationSession> sessions;
        lock (Lock)
        {
            var matches = Sessions
                .Where(pair => string.Equals(pair.Value.TranslationModelId, modelId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sessions = matches.Select(pair => pair.Value).ToList();
            foreach (var pair in matches) Sessions.Remove(pair.Key);
        }

        foreach (var session in sessions) session.Dispose();
    }
}
