using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Whisper.net;

namespace Ruse.App;

internal sealed record TranscriptionSnapshot(
    string Status,
    string Transcript,
    string Partial,
    string? Error,
    float Level,
    bool HasAudio,
    double ElapsedSeconds,
    bool? GpuActive,
    string? DetectedLanguage,
    IReadOnlyList<SubtitleCue> Segments);

internal sealed class TranscriptionSession : IDisposable
{
    private readonly int _tapId;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _textLock = new();
    private readonly List<SubtitleCue> _segments = new();
    private readonly StringBuilder _transcriptBuilder = new();
    private readonly Task _worker;
    private readonly long _startedAt = Environment.TickCount64;
    private string _baseStatus = "Starting";
    private string _transcript = "";
    private bool _transcriptDirty;
    private string _partial = "";
    private string? _error;
    private string? _detectedLanguage;
    private bool _modelReady;
    private bool _gpuActive;
    private bool _processing;
    private long _generation;
    private int _disposed;

    public TranscriptionSession(int tapId, string modelId)
    {
        _tapId = tapId;
        ModelId = modelId;
        _worker = Task.Run(RunAsync);
    }

    public string ModelId { get; }

    public TranscriptionSnapshot Snapshot(bool includeCues = false)
    {
        long lastAudio = TranscriptionProcessor.GetLastAudioTick(_tapId);
        bool hasAudio = lastAudio > 0 && Environment.TickCount64 - lastAudio < 2500;
        float level = TranscriptionProcessor.GetLevel(_tapId);

        lock (_textLock)
        {
            if (_transcriptDirty)
            {
                _transcript = _transcriptBuilder.ToString();
                _transcriptDirty = false;
            }

            string status = _error != null
                ? "Error"
                : !_modelReady
                    ? _baseStatus
                    : _processing ? "Transcribing" : hasAudio ? "Listening" : "Listening";
            return new TranscriptionSnapshot(
                status,
                _transcript,
                _partial,
                _error,
                level,
                hasAudio,
                Math.Max(0, (Environment.TickCount64 - _startedAt) / 1000.0),
                _modelReady ? _gpuActive : null,
                _detectedLanguage,
                includeCues ? _segments.ToArray() : Array.Empty<SubtitleCue>());
        }
    }

    public void Clear()
    {
        TranscriptionProcessor.ClearTap(_tapId);
        lock (_textLock)
        {
            _generation++;
            _transcript = "";
            _transcriptBuilder.Clear();
            _transcriptDirty = false;
            _partial = "";
            _error = null;
            _detectedLanguage = null;
            _segments.Clear();
            _processing = false;
            _baseStatus = "Listening";
        }
    }

    private async Task RunAsync()
    {
        WhisperProcessor? processor = null;
        bool speechAcquired = false;
        try
        {
            string? path = TranscriptionModels.GetInstalledPath(ModelId);
            if (path == null)
            {
                SetStatus("No speech model installed");
                return;
            }

            SetStatus("Checking speech model");
            var probe = await ModelProbe.EnsureAsync(ModelId, path, _cancel.Token);
            if (!probe.Success)
            {
                lock (_textLock) _error = probe.Message;
                SetStatus("Transcription error");
                AppLog.Warning(probe.Message);
                return;
            }

            SetStatus("Loading speech model");
            var model = TranscriptionModels.Find(ModelId);
            var factory = SpeechModelCache.Acquire(ModelId, path, _cancel.Token);
            if (factory == null)
            {
                lock (_textLock) _error = "Could not load the speech model.";
                SetStatus("Transcription error");
                return;
            }

            speechAcquired = true;
            try
            {
                processor = factory.CreateBuilder()
                    .WithLanguage(model?.Language == "English" ? "en" : "auto")
                    .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                    .WithNoContext()
                    .Build();
                _gpuActive = SpeechModelCache.IsGpu(ModelId);
                _modelReady = true;
                SetStatus("Listening");
                await RunRecognitionAsync(processor);
            }
            finally
            {
                if (processor != null) await processor.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (_textLock) _error = ex.Message;
            SetStatus("Transcription error");
            AppLog.Error("Offline transcription failed.", ex);
        }
        finally
        {
            if (speechAcquired)
            {
                SpeechModelCache.Release(ModelId);
            }
        }
    }

    private async Task RunRecognitionAsync(WhisperProcessor processor)
    {
        long generation;
        lock (_textLock)
        {
            generation = _generation;
        }

        using var vad = new VadSegmenter(_tapId, SileroVad.TryCreate());
        using var vosk = StreamingAsrOptions.SupportsEnglishPartials(ModelId) ? VoskStreamingRecognizer.TryCreate() : null;
        int partialFrames = 0;
        vad.SpeechStarted += () => SpeechActivityHub.PublishSpeechStarted(_tapId);
        vad.SpeechEnded += () =>
        {
            vosk?.Reset();
            partialFrames = 0;
            SpeechActivityHub.PublishSpeechEnded(_tapId);
        };
        vad.FrameAvailable += (frame, _) =>
        {
            if (vosk == null) return;
            vosk.Accept(frame);
            if (++partialFrames < 15) return;
            partialFrames = 0;
            string partial = vosk.Partial();
            if (partial.Length > 0) SetPartial(partial, generation);
        };

        // Keep reading the tap while Whisper is inferring. A blocked VAD loop
        // lets the shared 20 s tap overflow and silently drops speech.
        var segments = Channel.CreateBounded<SpeechSegment>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        async Task ConsumeAsync()
        {
            try
            {
                await foreach (var segment in segments.Reader.ReadAllAsync(_cancel.Token))
                {
                    await TranscribeAsync(processor, segment.Samples, segment.StartSeconds, generation);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error("Speech chunk consumer failed.", ex);
            }
        }

        Task consumer = ConsumeAsync();
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var segment = await vad.ReadAsync(_cancel.Token);
                if (segment != null)
                {
                    segments.Writer.TryWrite(segment);
                }
            }
        }
        finally
        {
            segments.Writer.TryComplete();
            try
            {
                await consumer;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task TranscribeAsync(WhisperProcessor processor, float[] chunk, double chunkStart, long generation)
    {
        lock (_textLock)
        {
            if (generation != _generation)
            {
                return;
            }

            _processing = true;
            _partial = "\u2026";
            _baseStatus = "Transcribing";
        }

        SpeechAudio.Normalize(chunk);
        var text = new StringBuilder();
        var cues = new List<SubtitleCue>();
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
                cues.AddRange(SentenceSplitter.Split(value, start, end));

                if (text.Length > 0) text.Append(' ');
                text.Append(value);
            }
        }
        finally
        {
            lock (_textLock)
            {
                if (generation == _generation)
                {
                    _processing = false;
                    _partial = "";
                    _baseStatus = "Listening";
                }
            }
        }

        AppendFinal(text.ToString(), cues, detected, generation);
    }

    private void SetPartial(string text, long generation)
    {
        lock (_textLock)
        {
            if (generation == _generation)
            {
                _partial = text;
            }
        }
    }

    private void AppendFinal(string text, List<SubtitleCue> cues, string? detected, long generation)
    {
        text = text.Trim();
        lock (_textLock)
        {
            if (generation != _generation)
            {
                return;
            }

            if (text.Length > 0)
            {
                if (_transcriptBuilder.Length > 0) _transcriptBuilder.Append(' ');
                _transcriptBuilder.Append(text);
                _transcriptDirty = true;
            }

            if (cues.Count > 0)
            {
                _segments.AddRange(cues);
            }

            if (!string.IsNullOrWhiteSpace(detected))
            {
                _detectedLanguage = detected;
            }

            _partial = "";
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
        bool finished = false;
        try
        {
            finished = _worker.Wait(TimeSpan.FromMilliseconds(1500));
        }
        catch
        {
        }

        // Leave the token alive if the worker is still unwinding; disposing it
        // under a native inference call turns cancellation into a crash.
        if (finished)
        {
            _cancel.Dispose();
        }
    }
}

internal static class TranscriptionRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<int, TranscriptionSession> Sessions = new();

    public static void Ensure(int tapId, string modelId)
    {
        if (tapId <= 0 || string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        TranscriptionSession? previous = null;
        lock (Lock)
        {
            if (Sessions.TryGetValue(tapId, out var current))
            {
                if (current.ModelId == modelId)
                {
                    return;
                }

                previous = current;
                Sessions.Remove(tapId);
            }

            TranscriptionProcessor.ClearTap(tapId);
        }

        // Stop the old reader before the replacement starts draining the tap.
        previous?.Dispose();

        lock (Lock)
        {
            if (!Sessions.ContainsKey(tapId))
            {
                Sessions[tapId] = new TranscriptionSession(tapId, modelId);
            }
        }
    }

    public static TranscriptionSnapshot? Snapshot(int tapId, bool includeCues = false)
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

    public static void Restart(int tapId, string modelId)
    {
        Stop(tapId);
        Ensure(tapId, modelId);
    }

    public static void Stop(int tapId)
    {
        TranscriptionSession? session;
        lock (Lock)
        {
            if (!Sessions.Remove(tapId, out session)) return;
        }

        session.Dispose();
    }

    public static void StopAll()
    {
        List<TranscriptionSession> sessions;
        lock (Lock)
        {
            sessions = Sessions.Values.ToList();
            Sessions.Clear();
        }

        foreach (var session in sessions) session.Dispose();
    }

    public static void StopModel(string modelId)
    {
        List<TranscriptionSession> sessions;
        lock (Lock)
        {
            var matches = Sessions
                .Where(pair => string.Equals(pair.Value.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sessions = matches.Select(pair => pair.Value).ToList();
            foreach (var pair in matches) Sessions.Remove(pair.Key);
        }

        foreach (var session in sessions) session.Dispose();
    }
}

/// <summary>One loaded Whisper model shared by every session that uses it.</summary>
internal static class SpeechModelCache
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (WhisperFactory Factory, int References, bool Gpu)> Models = new();
    private static readonly HashSet<string> PendingDrops = new();

    public static WhisperFactory? Acquire(string modelId, string path, CancellationToken token)
    {
        lock (Lock)
        {
            if (Models.TryGetValue(modelId, out var existing))
            {
                PendingDrops.Remove(modelId);
                Models[modelId] = (existing.Factory, existing.References + 1, existing.Gpu);
                return existing.Factory;
            }

            if (token.IsCancellationRequested)
            {
                return null;
            }
        }

        WhisperFactory? loaded = null;
        bool gpu = ModelRuntime.UseGpu;
        try
        {
            try
            {
                loaded = WhisperFactory.FromPath(path, new WhisperFactoryOptions
                {
                    UseGpu = gpu,
                });
            }
            catch (Exception gpuError) when (gpu)
            {
                AppLog.Warning(
                    $"GPU load failed for speech model '{modelId}'; falling back to CPU. " +
                    gpuError.GetBaseException().Message);
                ModelRuntime.ReportGpuFallback(
                    $"Speech model '{modelId}': {gpuError.GetBaseException().Message}");
                gpu = false;
                loaded = WhisperFactory.FromPath(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not load speech model '{modelId}'.", ex);
            return null;
        }

        lock (Lock)
        {
            if (Models.TryGetValue(modelId, out var existing))
            {
                PendingDrops.Remove(modelId);
                Models[modelId] = (existing.Factory, existing.References + 1, existing.Gpu);
                loaded.Dispose();
                return existing.Factory;
            }

            if (token.IsCancellationRequested || PendingDrops.Contains(modelId))
            {
                loaded.Dispose();
                return null;
            }

            Models[modelId] = (loaded, 1, gpu);
        }

        return loaded;
    }

    public static bool IsGpu(string modelId)
    {
        lock (Lock)
        {
            return Models.TryGetValue(modelId, out var entry) && entry.Gpu;
        }
    }

    public static void Release(string modelId)
    {
        WhisperFactory? dispose = null;
        lock (Lock)
        {
            if (!Models.TryGetValue(modelId, out var entry)) return;
            if (entry.References > 1)
            {
                Models[modelId] = (entry.Factory, entry.References - 1, entry.Gpu);
                return;
            }

            Models.Remove(modelId);
            PendingDrops.Remove(modelId);
            dispose = entry.Factory;
        }

        dispose.Dispose();
    }

    public static void Drop(string modelId)
    {
        WhisperFactory? factory = null;
        lock (Lock)
        {
            if (!Models.TryGetValue(modelId, out var entry)) return;
            if (entry.References > 0)
            {
                PendingDrops.Add(modelId);
                return;
            }

            Models.Remove(modelId);
            PendingDrops.Remove(modelId);
            factory = entry.Factory;
        }

        factory?.Dispose();
    }

    public static void DropAll()
    {
        List<WhisperFactory> factories;
        lock (Lock)
        {
            factories = Models.Where(pair => pair.Value.References == 0)
                .Select(pair => pair.Value.Factory).ToList();
            foreach (var pair in Models.Where(pair => pair.Value.References == 0).ToList())
            {
                Models.Remove(pair.Key);
            }

            PendingDrops.Clear();
        }

        foreach (var factory in factories) factory.Dispose();
    }
}
