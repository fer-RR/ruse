using System.Text.Json;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using Vosk;

namespace Ruse.App;

internal sealed record TranscriptionSnapshot(string Status, string Transcript, string Partial);

internal sealed class TranscriptionSession : IDisposable
{
    private readonly int _tapId;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _textLock = new();
    private readonly Task _worker;
    private string _status = "Starting";
    private string _transcript = "";
    private string _partial = "";

    public TranscriptionSession(int tapId, string modelId)
    {
        _tapId = tapId;
        ModelId = modelId;
        _worker = Task.Run(RunAsync);
    }

    public string ModelId { get; }

    public TranscriptionSnapshot Snapshot()
    {
        lock (_textLock)
        {
            return new TranscriptionSnapshot(_status, _transcript, _partial);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            string? path = TranscriptionModels.GetInstalledPath(ModelId);
            if (path == null)
            {
                SetStatus("No speech model installed");
                return;
            }

            SetStatus("Loading speech model");
            using var model = new Model(path);
            using var recognizer = new VoskRecognizer(model, 16000f);
            SetStatus("Listening");

            var buffer = new byte[8192];
            while (!_cancel.IsCancellationRequested)
            {
                int read = TranscriptionProcessor.ReadAvailable(_tapId, buffer);
                if (read > 0)
                {
                    if (recognizer.AcceptWaveform(buffer, read))
                    {
                        string text = ReadField(recognizer.Result(), "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            lock (_textLock)
                            {
                                _transcript = string.IsNullOrWhiteSpace(_transcript)
                                    ? text
                                    : _transcript + " " + text;
                                _partial = "";
                            }
                        }
                    }
                    else
                    {
                        string partial = ReadField(recognizer.PartialResult(), "partial");
                        lock (_textLock) _partial = partial;
                    }
                }

                await Task.Delay(60, _cancel.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus("Transcription error: " + ex.Message);
            AppLog.Error("Offline transcription failed.", ex);
        }
    }

    private void SetStatus(string status)
    {
        lock (_textLock) _status = status;
    }

    private static string ReadField(string json, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(field, out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }
}

internal static class TranscriptionRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<int, TranscriptionSession> Sessions = new();

    public static void Ensure(int tapId, string modelId)
    {
        if (tapId <= 0) return;
        TranscriptionSession? previous = null;
        lock (Lock)
        {
            if (Sessions.TryGetValue(tapId, out var current))
            {
                if (current.ModelId == modelId) return;
                previous = current;
                Sessions.Remove(tapId);
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
}
