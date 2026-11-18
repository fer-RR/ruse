using System.Text.Json;
using Ruse.App.Diagnostics;
using Vosk;

namespace Ruse.App;

/// <summary>
/// Streaming partial recognizer backed by Vosk. Whisper remains the final
/// recognizer; this class only supplies live partial text.
/// </summary>
internal sealed class VoskStreamingRecognizer : IDisposable
{
    private readonly Model? _model;
    private readonly VoskRecognizer? _recognizer;
    private bool _disposed;

    private VoskStreamingRecognizer(Model model)
    {
        _model = model;
        _recognizer = new VoskRecognizer(model, 16000f);
        _recognizer.SetWords(false);
    }

    public static VoskStreamingRecognizer? TryCreate()
    {
        if (!VoskModels.IsInstalled) return null;
        var model = VoskModelCache.Acquire();
        if (model == null) return null;
        try
        {
            return new VoskStreamingRecognizer(model);
        }
        catch (Exception ex)
        {
            VoskModelCache.Release();
            AppLog.Warning("Could not start Vosk streaming ASR. " + ex.GetBaseException().Message);
            return null;
        }
    }

    public void Accept(ReadOnlySpan<float> samples)
    {
        if (_recognizer == null || _disposed || samples.IsEmpty) return;
        float[] frame = samples.ToArray();
        try
        {
            if (_recognizer.AcceptWaveform(frame, frame.Length))
            {
                // Vosk saw its own endpoint. Consume the final result so the
                // next partial starts cleanly; Whisper still owns the final text.
                _ = _recognizer.Result();
            }
        }
        catch
        {
        }
    }

    public string Partial()
    {
        if (_recognizer == null || _disposed) return "";
        try
        {
            using var document = JsonDocument.Parse(_recognizer.PartialResult());
            return document.RootElement.TryGetProperty("partial", out var value)
                ? value.GetString()?.Trim() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public void Reset()
    {
        if (_recognizer == null || _disposed) return;
        try { _recognizer.Reset(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _recognizer?.Dispose(); } catch { }
        VoskModelCache.Release();
    }
}

internal static class VoskModelCache
{
    private static readonly object Lock = new();
    private static Model? _model;
    private static int _references;

    public static Model? Acquire()
    {
        lock (Lock)
        {
            if (!VoskModels.IsInstalled) return null;
            try
            {
                _model ??= new Model(VoskModels.PathFor);
                _references++;
                return _model;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Could not load the Vosk model. " + ex.GetBaseException().Message);
                _model = null;
                return null;
            }
        }
    }

    public static void Release()
    {
        lock (Lock)
        {
            if (_references > 0) _references--;
            if (_references > 0 || _model == null) return;
            try { _model.Dispose(); } catch { }
            _model = null;
        }
    }
}

internal static class StreamingAsrOptions
{
    public static bool Enabled { get; set; }
}
