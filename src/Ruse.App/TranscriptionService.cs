using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using System.Text;
using Whisper.net;

namespace Ruse.App;

internal sealed record TranscriptionSnapshot(
    string Status,
    string Transcript,
    string Partial,
    string? Error,
    float Level,
    bool HasAudio,
    double ElapsedSeconds);

internal sealed class TranscriptionSession : IDisposable
{
    private const int SampleRate = 16000;
    private const int MinChunkSamples = SampleRate * 2;
    private const int MaxChunkSamples = SampleRate * 10;
    private readonly int _tapId;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _textLock = new();
    private readonly object _sampleLock = new();
    private readonly List<float> _samples = new(MaxChunkSamples + 8192);
    private readonly Task _worker;
    private readonly long _startedAt = Environment.TickCount64;
    private string _baseStatus = "Starting";
    private string _transcript = "";
    private string _partial = "";
    private string? _error;
    private bool _modelReady;
    private bool _processing;
    private int _disposed;

    public TranscriptionSession(int tapId, string modelId)
    {
        _tapId = tapId;
        ModelId = modelId;
        _worker = Task.Run(RunAsync);
    }

    public string ModelId { get; }

    public TranscriptionSnapshot Snapshot()
    {
        long lastAudio = TranscriptionProcessor.GetLastAudioTick(_tapId);
        bool hasAudio = lastAudio > 0 && Environment.TickCount64 - lastAudio < 2500;
        float level = TranscriptionProcessor.GetLevel(_tapId);

        lock (_textLock)
        {
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
                Math.Max(0, (Environment.TickCount64 - _startedAt) / 1000.0));
        }
    }

    public void Clear()
    {
        TranscriptionProcessor.ClearTap(_tapId);
        lock (_sampleLock) _samples.Clear();
        lock (_textLock)
        {
            _transcript = "";
            _partial = "";
            _error = null;
        }
    }

    private async Task RunAsync()
    {
        WhisperProcessor? processor = null;
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

            try
            {
                processor = factory.CreateBuilder()
                    .WithLanguage(model?.Language == "English" ? "en" : "auto")
                    .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                    .WithNoContext()
                    .Build();
                _modelReady = true;
                SetStatus("Listening");
                await RunRecognitionAsync(processor);
            }
            finally
            {
                processor?.Dispose();
                SpeechModelCache.Release(ModelId);
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
                await TranscribeAsync(processor, chunk);
            }

            await Task.Delay(40, _cancel.Token);
        }
    }

    private async Task TranscribeAsync(WhisperProcessor processor, float[] chunk)
    {
        lock (_textLock)
        {
            _processing = true;
            _partial = "…";
            _baseStatus = "Transcribing";
        }

        var text = new StringBuilder();
        try
        {
            await foreach (var segment in processor.ProcessAsync(chunk, _cancel.Token))
            {
                string value = segment.Text.Trim();
                if (value.Length == 0 || (value.StartsWith('[') && value.EndsWith(']')))
                {
                    continue;
                }

                if (text.Length > 0) text.Append(' ');
                text.Append(value);
            }
        }
        finally
        {
            lock (_textLock)
            {
                _processing = false;
                _partial = "";
                _baseStatus = "Listening";
            }
        }

        AppendFinal(text.ToString());
    }

    private void AppendFinal(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        lock (_textLock)
        {
            _transcript = _transcript.Length == 0 ? text : _transcript + " " + text;
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
        try
        {
            _worker.Wait(TimeSpan.FromMilliseconds(1500));
        }
        catch
        {
        }

        _cancel.Dispose();
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
                TranscriptionProcessor.ClearTap(tapId);
            }
            else
            {
                TranscriptionProcessor.ClearTap(tapId);
            }

            Sessions[tapId] = new TranscriptionSession(tapId, modelId);
        }

        previous?.Dispose();
    }

    public static TranscriptionSnapshot? Snapshot(int tapId)
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
    private static readonly Dictionary<string, (WhisperFactory Factory, int References)> Models = new();
    private static readonly HashSet<string> PendingDrops = new();

    public static WhisperFactory? Acquire(string modelId, string path, CancellationToken token)
    {
        lock (Lock)
        {
            if (Models.TryGetValue(modelId, out var existing))
            {
                PendingDrops.Remove(modelId);
                Models[modelId] = (existing.Factory, existing.References + 1);
                return existing.Factory;
            }

            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var factory = WhisperFactory.FromPath(path);
                if (token.IsCancellationRequested)
                {
                    factory.Dispose();
                    return null;
                }

                Models[modelId] = (factory, 1);
                return factory;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not load speech model '{modelId}'.", ex);
                return null;
            }
        }
    }

    public static void Release(string modelId)
    {
        lock (Lock)
        {
            if (!Models.TryGetValue(modelId, out var entry)) return;
            if (entry.References > 1)
            {
                Models[modelId] = (entry.Factory, entry.References - 1);
                return;
            }

            Models.Remove(modelId);
            PendingDrops.Remove(modelId);
            entry.Factory.Dispose();
        }
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
