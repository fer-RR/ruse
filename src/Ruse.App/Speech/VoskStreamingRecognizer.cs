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
    private float[] _frame = Array.Empty<float>();
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
        if (_frame.Length < samples.Length) _frame = new float[samples.Length];
        samples.CopyTo(_frame);
        try
        {
            if (_recognizer.AcceptWaveform(_frame, samples.Length))
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
    private static bool _deletePending;

    public static Model? Acquire()
    {
        lock (Lock)
        {
            if (_deletePending || !VoskModels.IsInstalled) return null;
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
            if (_references > 0) return;
            DisposeModelLocked();
            if (_deletePending)
            {
                _deletePending = false;
                VoskModels.DeleteFiles();
            }
        }
    }

    /// <summary>Stops new users and disposes the model now or when the last user releases it.</summary>
    public static void Delete()
    {
        lock (Lock)
        {
            _deletePending = true;
            if (_references == 0)
            {
                DisposeModelLocked();
            }
        }
    }

    private static void DisposeModelLocked()
    {
        if (_model == null) return;
        try { _model.Dispose(); } catch { }
        _model = null;
    }
}

internal static class StreamingAsrOptions
{
    public static bool Enabled { get; set; }

    public static bool SupportsEnglishPartials(string? speechModelId) =>
        Enabled &&
        string.Equals(
            TranscriptionModels.Find(speechModelId)?.Language,
            "English",
            StringComparison.OrdinalIgnoreCase);
}
