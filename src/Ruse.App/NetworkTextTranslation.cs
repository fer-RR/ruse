using System.Text;
using System.Threading.Channels;
using LLama;
using LLama.Common;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record NetworkTextTranslationSnapshot(
    string Status,
    string SourceText,
    string TranslatedText,
    string? Error);

/// <summary>Translates new network chat messages without going through the audio tap.</summary>
internal sealed class NetworkTextTranslation : IDisposable
{
    private const int TranslationContextSize = 2048;
    private const int MaxTextCharacters = 200_000;
    private const int MaxQueuedMessages = 64;

    private readonly string _targetCode;
    private readonly string _translationModelId;
    private readonly string _provider;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly object _gate = new();
    private readonly StringBuilder _source = new();
    private readonly StringBuilder _translated = new();
    private readonly Task _worker;
    private string _status = "Starting";
    private string? _error;
    private string _sourceSnapshot = "";
    private string _translatedSnapshot = "";
    private bool _sourceDirty;
    private bool _translatedDirty;
    private bool _ready;
    private bool _disposed;

    public NetworkTextTranslation(string? translationModelId, string targetCode, string provider)
    {
        _translationModelId = TranslationModels.Resolve(translationModelId);
        _targetCode = TranslationLanguages.Normalize(targetCode);
        _provider = string.Equals(provider, "opus", StringComparison.OrdinalIgnoreCase)
            ? "opus"
            : "qwen";
        _worker = Task.Run(RunAsync);
        _ = _worker.ContinueWith(_ =>
        {
            _cancel.Dispose();
        }, TaskScheduler.Default);
        _ = _worker.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
    }

    public void Submit(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            _status = _ready ? "Translating" : "Starting";
            _error = null;
        }

        string normalized = text.Trim();
        if (normalized.Length > MaxTextCharacters)
        {
            normalized = normalized[..MaxTextCharacters];
        }

        if (!_queue.Writer.TryWrite(normalized))
        {
            SetError("Translation is behind; some text was dropped.");
        }
    }

    public NetworkTextTranslationSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_sourceDirty)
            {
                _sourceSnapshot = _source.ToString();
                _sourceDirty = false;
            }

            if (_translatedDirty)
            {
                _translatedSnapshot = _translated.ToString();
                _translatedDirty = false;
            }

            return new NetworkTextTranslationSnapshot(
                _status,
                _sourceSnapshot,
                _translatedSnapshot,
                _error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync()
    {
        StatelessExecutor? qwen = null;
        string? acquiredModel = null;
        try
        {
            string? qwenPath = TranslationModels.GetInstalledPath(_translationModelId);
            if (qwenPath != null)
            {
                var probe = await TranslationModelProbe.EnsureAsync(
                    _translationModelId, qwenPath, _cancel.Token);
                if (!probe.Success)
                {
                    SetError(probe.Message);
                    return;
                }

                var weights = TranslationModelCache.Acquire(
                    _translationModelId, qwenPath, _cancel.Token);
                if (weights == null)
                {
                    SetError("Could not load the translation model.");
                    return;
                }

                acquiredModel = _translationModelId;
                int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                try
                {
                    qwen = new StatelessExecutor(
                        weights,
                        ModelRuntime.TranslationParams(
                            qwenPath, 256, TranslationContextSize, threads))
                    {
                        ApplyTemplate = true,
                        SystemMessage = TranslationPrompt.System(_targetCode),
                    };
                }
                catch (Exception gpuError) when (ModelRuntime.UseGpu)
                {
                    AppLog.Warning(
                        "GPU context creation failed for text chat translation; falling back to CPU. " +
                        gpuError.GetBaseException().Message);
                    ModelRuntime.ReportGpuFallback(
                        "Text chat translation: " + gpuError.GetBaseException().Message);
                    qwen = new StatelessExecutor(weights, new ModelParams(qwenPath)
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
            else if (_provider != "opus")
            {
                SetError("No translation model installed.");
                return;
            }

            OpusMtEngine? opus = qwen == null && _provider == "opus" ? new OpusMtEngine() : null;
            if (opus == null && qwen == null)
            {
                SetError("No translation model installed.");
                return;
            }

            lock (_gate)
            {
                _ready = true;
                _status = "Ready";
                _error = null;
            }

            await foreach (string text in _queue.Reader.ReadAllAsync(_cancel.Token))
            {
                _cancel.Token.ThrowIfCancellationRequested();
                string translated;
                lock (_gate)
                {
                    _status = "Translating";
                    _error = null;
                }

                try
                {
                    string? cached = TranslationMemory.TryGet(
                        _provider, _translationModelId, _targetCode, null, text);
                    if (cached != null)
                    {
                        translated = cached;
                    }
                    else if (qwen != null)
                    {
                        translated = await TranslationSession.TranslateTextAsync(
                            qwen, text, _cancel.Token);
                        TranslationMemory.Store(
                            _provider, _translationModelId, _targetCode, null, text, translated);
                    }
                    else
                    {
                        translated = await Task.Run(
                            () => opus!.Translate(text, "en", _targetCode, _cancel.Token),
                            _cancel.Token);
                        TranslationMemory.Store(
                            _provider, _translationModelId, _targetCode, null, text, translated);
                    }

                    lock (_gate)
                    {
                        AppendBounded(_source, text);
                        AppendBounded(_translated, translated.Trim());
                        _sourceDirty = true;
                        _translatedDirty = true;
                        _status = "Ready";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SetError("Translation failed: " + ex.Message);
                    AppLog.Error("Network text translation failed.", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            AppLog.Error("Network text translation stopped.", ex);
        }
        finally
        {
            (qwen as IDisposable)?.Dispose();
            if (acquiredModel != null)
            {
                TranslationModelCache.Release(acquiredModel);
            }
        }
    }

    private static void AppendBounded(StringBuilder target, string text)
    {
        if (target.Length > 0)
        {
            target.Append('\n');
        }

        target.Append(text);
        if (target.Length > MaxTextCharacters)
        {
            target.Remove(0, target.Length - MaxTextCharacters);
        }
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _status = "Translation error";
            _error = message;
        }
    }
}
