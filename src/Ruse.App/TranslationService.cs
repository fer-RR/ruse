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
    string? DetectedLanguage,
    bool? GpuActive,
    IReadOnlyList<SubtitleCue> SourceSegments,
    IReadOnlyList<SubtitleCue> TranslatedSegments);

internal sealed class TranslationSession : IDisposable
{
    private const int TranslationContextSize = 2048;

    private static readonly SemaphoreSlim TranslationGate = new(1, 1);

    private readonly int _tapId;
    private readonly string _speechModelId;
    private readonly string _translationModelId;
    private readonly string _targetCode;
    private readonly string _provider;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _textLock = new();
    private readonly object _sampleLock = new();
    private readonly SpeechChunker _chunker = new();
    private readonly List<SubtitleCue> _sourceCues = new();
    private readonly List<SubtitleCue> _translatedCues = new();
    private float[] _decoded = new float[8192];
    private readonly Task _worker;
    private readonly long _startedAt = Environment.TickCount64;

    private StatelessExecutor? _translator;
    private OpusMtEngine? _opus;
    private string _baseStatus = "Starting";
    private string _sourceText = "";
    private string _sourcePartial = "";
    private string _translatedText = "";
    private string _translatedPartial = "";
    private string? _error;
    private string? _detectedLanguage;
    private bool _modelReady;
    private bool _gpuActive;
    private bool _processing;
    private int _disposed;

    public TranslationSession(int tapId, string speechModelId, string translationModelId, string targetCode, string translationProvider = "qwen")
    {
        _tapId = tapId;
        _speechModelId = speechModelId;
        _translationModelId = translationModelId;
        _targetCode = TranslationLanguages.Normalize(targetCode);
        _provider = string.Equals(translationProvider, "opus", StringComparison.OrdinalIgnoreCase)
            ? "opus" : "qwen";
        _worker = Task.Run(RunAsync);
    }

    public string SpeechModelId => _speechModelId;
    public string TranslationModelId => _translationModelId;
    public string TargetCode => _targetCode;
    public string Provider => _provider;

    public TranslationSnapshot Snapshot(bool includeCues = false)
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
                _detectedLanguage,
                _modelReady ? _gpuActive : null,
                includeCues ? _sourceCues.ToArray() : Array.Empty<SubtitleCue>(),
                includeCues ? _translatedCues.ToArray() : Array.Empty<SubtitleCue>());
        }
    }

    public void Clear()
    {
        TranscriptionProcessor.ClearTap(_tapId);
        lock (_sampleLock) _chunker.Clear();
        lock (_textLock)
        {
            _sourceText = "";
            _sourcePartial = "";
            _translatedText = "";
            _translatedPartial = "";
            _error = null;
            _detectedLanguage = null;
            _sourceCues.Clear();
            _translatedCues.Clear();
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

            if (_provider == "opus")
            {
                _opus = new OpusMtEngine();
            }
            if (TranslationLanguages.NeedsModel(_targetCode) &&
                (_provider != "opus" || _targetCode is "ja" or "ko"))
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
                int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                try
                {
                    _translator = new StatelessExecutor(
                        weights,
                        ModelRuntime.TranslationParams(translationPath, 256, TranslationContextSize, threads))
                    {
                        ApplyTemplate = true,
                        SystemMessage = TranslationPrompt.System(_targetCode),
                    };
                }
                catch (Exception gpuError) when (ModelRuntime.UseGpu)
                {
                    AppLog.Warning(
                        "GPU context creation failed for translation; falling back to CPU. " +
                        gpuError.GetBaseException().Message);
                    ModelRuntime.ReportGpuFallback(
                        "Translation context: " + gpuError.GetBaseException().Message);
                    _translator = new StatelessExecutor(weights, new ModelParams(translationPath)
                    {
                        ContextSize = TranslationContextSize,
                        BatchSize = 256,
                        GpuLayerCount = 0,
                        Threads = threads,
                    })
                    {
                        ApplyTemplate = true,
                        SystemMessage = TranslationPrompt.System(_targetCode),
                    };
                }
            }

            try
            {
                var builder = factory.CreateBuilder()
                    .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                    .WithNoContext();
                builder = _targetCode == TranslationLanguages.English && _provider != "opus"
                    ? builder.WithTranslate()
                    : builder.WithLanguage("auto");

                processor = builder.Build();
                bool speechGpu = SpeechModelCache.IsGpu(_speechModelId);
                bool qwenFallback = _provider == "opus" &&
                                    _targetCode is "ja" or "ko" && _translator != null;
                bool translationGpu = (_provider != "opus" || qwenFallback) &&
                                      (!TranslationLanguages.NeedsModel(_targetCode) ||
                                       string.IsNullOrEmpty(_translationModelId) ||
                                       TranslationModelCache.IsGpu(_translationModelId));
                _gpuActive = speechGpu && translationGpu;
                _modelReady = true;
                SetStatus("Listening");
                await RunRecognitionAsync(processor);
            }
            finally
            {
                try
                {
                    if (processor != null) await processor.DisposeAsync();
                }
                finally
                {
                    SpeechModelCache.Release(_speechModelId);
                }
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
            double chunkStart = 0;
            if (read > 1)
            {
                int sampleCount = read / 2;
                lock (_sampleLock)
                {
                    if (_decoded.Length < sampleCount)
                    {
                        _decoded = new float[sampleCount];
                    }

                    for (int i = 0; i < sampleCount; i++)
                    {
                        short value = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
                        _decoded[i] = value / 32768f;
                    }

                    if (_chunker.Add(_decoded.AsSpan(0, sampleCount), out var produced, out var start))
                    {
                        chunk = produced;
                        chunkStart = start;
                    }
                }
            }

            if (chunk != null)
            {
                await TranslateChunkAsync(processor, chunk, chunkStart);
            }

            await Task.Delay(40, _cancel.Token);
        }
    }

    private async Task TranslateChunkAsync(WhisperProcessor processor, float[] chunk, double chunkStart)
    {
        lock (_textLock)
        {
            _processing = true;
            _sourcePartial = "…";
            _baseStatus = "Transcribing";
        }

        var source = new StringBuilder();
        var sourceCues = new List<SubtitleCue>();
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

                double start = chunkStart + segment.Start.TotalSeconds;
                double end = chunkStart + segment.End.TotalSeconds;
                sourceCues.AddRange(SentenceSplitter.Split(value, start, end));

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

        AppendSource(text, sourceCues, detected);

        if ((_provider == "qwen" && _targetCode == TranslationLanguages.English) ||
            TranslationLanguages.MatchesSource(_targetCode, detected))
        {
            AppendTranslation(text, sourceCues);
        }
        else if (_provider == "opus" && _opus != null && _targetCode is not "ja" and not "ko")
        {
            lock (_textLock)
            {
                _baseStatus = "Translating";
                _translatedPartial = "...";
            }

            try
            {
                string translated = await Task.Run(
                    () => _opus.Translate(text, detected, _targetCode, _cancel.Token),
                    _cancel.Token);
                AppendTranslation(translated, SentenceSplitter.Align(sourceCues, translated));
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

                AppLog.Error("OPUS-MT inference failed.", ex);
            }
            finally
            {
                lock (_textLock)
                {
                    _translatedPartial = "";
                }
            }
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
                AppendTranslation(translated, SentenceSplitter.Align(sourceCues, translated));
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

    private void AppendSource(string text, List<SubtitleCue> cues, string? detected)
    {
        lock (_textLock)
        {
            _sourceText = _sourceText.Length == 0 ? text : _sourceText + " " + text;
            if (cues.Count > 0)
            {
                _sourceCues.AddRange(cues);
            }

            if (!string.IsNullOrWhiteSpace(detected))
            {
                _detectedLanguage = detected;
            }
        }
    }

    private void AppendTranslation(string text, List<SubtitleCue> cues)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        lock (_textLock)
        {
            _translatedText = _translatedText.Length == 0
                ? text
                : _translatedText + " " + text;
            if (cues.Count > 0)
            {
                _translatedCues.AddRange(cues);
            }

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
        string targetCode,
        string translationProvider = "qwen")
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
                    string.Equals(current.TargetCode, targetCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.Provider, translationProvider, StringComparison.OrdinalIgnoreCase))
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

            Sessions[tapId] = new TranslationSession(
                tapId, speechModelId, translationModelId, targetCode, translationProvider);
        }

        previous?.Dispose();
    }

    public static TranslationSnapshot? Snapshot(int tapId, bool includeCues = false)
    {
        lock (Lock)
        {
            return Sessions.TryGetValue(tapId, out var session) ? session.Snapshot(includeCues) : null;
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
        string targetCode,
        string translationProvider = "qwen")
    {
        Stop(tapId);
        Ensure(tapId, speechModelId, translationModelId, targetCode, translationProvider);
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
