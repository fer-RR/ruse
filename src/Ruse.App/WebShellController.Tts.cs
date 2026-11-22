using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private bool TryGetTts(string id, out NodeEntry entry)
    {
        if (_nodes.TryGetValue(id, out entry!) && entry.Source is { Kind: SourceKind.Tts })
        {
            return true;
        }

        entry = null!;
        return false;
    }

    private void SetTtsModel(string id, string model)
    {
        string resolved = TtsModels.Find(model)?.Id ?? TtsModels.DefaultId;
        if (TryGetTts(id, out var entry))
        {
            entry.TtsModel = resolved;
            entry.TtsStatus = TtsModels.IsInstalled(resolved) ? "Ready" : "No model installed";
            PublishGraph();
            return;
        }

        _settings.TtsModel = resolved;
        _settings.Save();
        PublishSettings();
    }

    private async void DownloadTtsModel(string modelId)
    {
        var model = TtsModels.Find(modelId);
        if (model == null)
        {
            return;
        }

        if (_ttsDownloadCancel != null)
        {
            SetStatus("Another text-to-speech model is already downloading.", isError: true);
            return;
        }

        var cancel = new CancellationTokenSource();
        _ttsDownloadCancel = cancel;
        var progress = new Progress<TtsModelProgress>(PublishTtsProgress);
        try
        {
            await TtsModels.DownloadAsync(modelId, progress, cancel.Token);
            if (!TtsModels.IsInstalled(_settings.TtsModel))
            {
                _settings.TtsModel = modelId;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry =>
                         entry.Source?.Kind == SourceKind.Tts &&
                         (string.IsNullOrEmpty(entry.TtsModel) ||
                          !TtsModels.IsInstalled(entry.TtsModel) ||
                          string.Equals(entry.TtsModel, modelId, StringComparison.OrdinalIgnoreCase))))
            {
                entry.TtsModel = modelId;
                entry.TtsStatus = "Ready";
            }

            PublishSettings();
            PublishGraph();
            SetStatus($"{model.Name} installed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{model.Name} download cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"{model.Name} download failed: {ex.Message}", isError: true);
        }
        finally
        {
            _ttsDownloadCancel = null;
            cancel.Dispose();
            PublishSettings();
        }
    }

    private void PublishTtsProgress(TtsModelProgress progress)
    {
        _ttsDownload = progress;
        long now = Environment.TickCount64;
        bool terminal = progress.Phase is "done" or "error" or "cancelled";
        if (!terminal && now - _lastTtsPublish < 100)
        {
            return;
        }

        _lastTtsPublish = now;
        PublishSettings();
    }

    private void CancelTtsDownload()
    {
        _ttsDownloadCancel?.Cancel();
        SetStatus("Cancelling text-to-speech model download...");
    }

    private async void DeleteTtsModel(string modelId)
    {
        if (!TtsModels.IsInstalled(modelId))
        {
            return;
        }

        try
        {
            if (_ttsDownload != null &&
                string.Equals(_ttsDownload.Id, modelId, StringComparison.OrdinalIgnoreCase))
            {
                _ttsDownloadCancel?.Cancel();
            }

            foreach (var entry in _nodes.Values.Where(entry => entry.Source?.Kind == SourceKind.Tts &&
                         string.Equals(entry.TtsModel, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                entry.TtsGenerationCancel?.Cancel();
                entry.TtsGenerating = false;
                entry.Source?.Stop();
                entry.TtsStatus = "No model installed";
            }

            await TtsModels.DeleteAsync(modelId).ConfigureAwait(true);
            if (string.Equals(_settings.TtsModel, modelId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.TtsModel = TtsModels.DefaultId;
                _settings.Save();
            }

            PublishSettings();
            PublishGraph();
            SetStatus($"{TtsModels.Find(modelId)?.Name ?? "Text-to-speech model"} deleted.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not delete the text-to-speech model: {ex.Message}", isError: true);
        }
    }

    private void OpenTtsModelsFolder()
    {
        try
        {
            Directory.CreateDirectory(TtsModels.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = TtsModels.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the text-to-speech models folder: {ex.Message}", isError: true);
        }
    }

    private void SetTtsText(string id, string text)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        entry.TtsText = text;
        entry.TtsSpokenLength = 0;
        entry.TtsStatus = TtsModels.IsInstalled(entry.TtsModel ?? _settings.TtsModel) ? "Ready" : "No model installed";
        PublishGraph();
    }

    private void SetTtsSource(string id, string sourceNodeId)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        entry.TtsSourceNodeId = string.IsNullOrWhiteSpace(sourceNodeId) ? null : sourceNodeId;
        foreach (var link in _links.Where(link =>
                     link.Kind == "barge-key" &&
                     link.To == entry.Id &&
                     string.Equals(link.From, entry.TtsSourceNodeId, StringComparison.Ordinal)).ToList())
        {
            RemoveLinkInternal(link);
        }
        entry.TtsSpokenLength = 0;
        if (entry.TtsSourceNodeId != null &&
            _nodes.TryGetValue(entry.TtsSourceNodeId, out var producer) &&
            (producer.Source is { Kind: SourceKind.Network } || producer.TranslatorText != null) &&
            TextFromSource(entry.TtsSourceNodeId) is { } existing)
        {
            entry.TtsSpokenLength = existing.Text.Length;
        }

        entry.TtsStatus = TtsModels.IsInstalled(entry.TtsModel ?? _settings.TtsModel) ? "Ready" : "No model installed";
        PublishGraph();
    }

    private void SetTtsSpeed(string id, float speed)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        entry.TtsSpeed = Math.Clamp(float.IsFinite(speed) ? speed : 1f, 0.5f, 2f);
        PublishGraph();
    }

    private void SetTtsVoice(string id, int voice)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }
        entry.TtsVoice = Math.Max(0, voice);
        PublishGraph();
    }
    
    private void ChooseTtsTextFile(string id)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a text file for speech",
            Filter = "Text files|*.txt;*.md;*.srt;*.vtt|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            const int maxChars = 20000;
            var text = new StringBuilder(maxChars);
            using (var reader = new StreamReader(dialog.FileName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var buffer = new char[8192];
                while (text.Length < maxChars)
                {
                    int read = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - text.Length));
                    if (read <= 0) break;
                    text.Append(buffer, 0, read);
                }
            }

            entry.TtsText = text.ToString();
            entry.TtsSpokenLength = 0;
            entry.TtsStatus = TtsModels.IsInstalled(entry.TtsModel ?? _settings.TtsModel) ? "Ready" : "No model installed";
            PublishGraph();
            SetStatus($"Loaded text from {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read the text file: {ex.Message}", isError: true);
        }
    }

    private void SpeakTts(string id)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        _ = SpeakTtsAsync(entry, entry.TtsText ?? "");
    }

    private void StopTts(string id)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        entry.TtsGenerationCancel?.Cancel();
        entry.TtsGenerating = false;
        entry.Source!.Stop();
        entry.TtsStatus = TtsModels.IsInstalled(entry.TtsModel ?? _settings.TtsModel) ? "Ready" : "No model installed";
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void JumpTtsToLatest(string id)
    {
        if (!TryGetTts(id, out var entry))
        {
            return;
        }

        string text = !string.IsNullOrWhiteSpace(entry.TtsSourceNodeId) &&
                      TextFromSource(entry.TtsSourceNodeId) is { } source
            ? source.Text
            : entry.TtsText ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clauses = SpeechText.Clauses(text);
        string latest = clauses.Count > 0 ? clauses[^1] : text.Trim();
        if (latest.Length == 0)
        {
            return;
        }

        entry.TtsGenerationCancel?.Cancel();
        entry.TtsGenerating = false;
        entry.Source?.Stop();
        entry.TtsSpokenLength = text.Length;
        entry.TtsText = text;
        entry.TtsStatus = "Ready";
        PublishGraph();
        PublishTelemetry(force: true);
        _ = SpeakTtsAsync(entry, latest, entry.TtsSpeed);
        SetStatus("Jumped to the latest text.");
    }

    private async Task SpeakStreamingTtsAsync(NodeEntry entry, string modelId, string text, float? speedOverride)
    {
        if (entry.Source == null) return;
        var cancel = new CancellationTokenSource();
        entry.TtsGenerationCancel?.Cancel();
        entry.TtsGenerationCancel = cancel;
        entry.TtsGenerating = true;
        entry.TtsStatus = "Generating";
        entry.TtsModel = modelId;
        entry.Source.StartTtsStream();
        PublishGraph();

        try
        {
            await using var provider = StreamingTtsProviders.Create(modelId);
            float speed = Math.Clamp(speedOverride ?? entry.TtsSpeed, 0.5f, 2f);
            var clauses = SpeechText.Clauses(text);
            if (clauses.Count == 0) clauses.Add(text.Trim());
            foreach (string clause in clauses)
            {
                cancel.Token.ThrowIfCancellationRequested();
                await provider.StreamAsync(
                    clause,
                    speed,
                    entry.TtsVoice,
                    (samples, frames) =>
                    {
                        if (cancel.IsCancellationRequested ||
                            _disposed ||
                            !ReferenceEquals(entry.TtsGenerationCancel, cancel) ||
                            !_nodes.TryGetValue(entry.Id, out var current) ||
                            current.Source == null)
                        {
                            return;
                        }
                        try
                        {
                            current.Source.EnqueueTtsSamples(samples.AsSpan(0, frames * 2), cancel.Token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    },
                    cancel.Token);
            }
            if (_nodes.TryGetValue(entry.Id, out var node) && node.Source != null)
            {
                node.Source.CompleteTtsStream();
                node.TtsGenerating = false;
                node.TtsStatus = "Speaking";
                PublishGraph();
                PublishTelemetry(force: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_nodes.TryGetValue(entry.Id, out var node))
            {
                node.TtsGenerating = false;
                node.TtsStatus = "Error: " + ex.Message;
                node.Source?.CompleteTtsStream();
            }
            PublishGraph();
            SetStatus("Text-to-speech failed: " + ex.Message, isError: true);
        }
        finally
        {
            bool current = ReferenceEquals(entry.TtsGenerationCancel, cancel);
            if (current)
            {
                entry.TtsGenerationCancel = null;
            }

            if (current && cancel.IsCancellationRequested && _nodes.TryGetValue(entry.Id, out var stopped))
            {
                stopped.TtsGenerating = false;
                stopped.Source?.Stop();
                PublishGraph();
            }

            cancel.Dispose();
        }
    }

    private async Task SpeakTtsAsync(NodeEntry entry, string text, float? speedOverride = null)
    {
        if (entry.TtsGenerating)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("There is no text to speak.", isError: true);
            return;
        }

        string modelId = TtsModels.IsInstalled(entry.TtsModel)
            ? entry.TtsModel!
            : TtsModels.IsInstalled(_settings.TtsModel)
                ? _settings.TtsModel
                : TtsModels.All.FirstOrDefault(model => TtsModels.IsInstalled(model.Id))?.Id ?? "";
        if (string.IsNullOrEmpty(modelId))
        {
            entry.TtsStatus = "No model installed";
            PublishGraph();
            SetStatus("Install a text-to-speech model in Settings > Speech & translation.", isError: true);
            return;
        }

        if (TtsModels.Find(modelId)?.Family is "kokoro-sharp" or "piper")
        {
            await SpeakStreamingTtsAsync(entry, modelId, text, speedOverride);
            return;
        }

        var clauses = SpeechText.Clauses(text);
        if (clauses.Count == 0)
        {
            clauses.Add(text.Trim());
        }

        float speed = Math.Clamp(speedOverride ?? entry.TtsSpeed, 0.5f, 2f);

        var cancel = new CancellationTokenSource();
        entry.TtsGenerationCancel?.Cancel();
        entry.TtsGenerationCancel = cancel;
        entry.TtsGenerating = true;
        entry.TtsStatus = "Generating";
        entry.TtsModel = modelId;
        PublishGraph();
        try
        {
            bool started = false;
            await foreach (string path in TtsSynthesis.SynthesizeClausesAsync(
                modelId, clauses, speed, entry.TtsVoice, cancel.Token))
            {
                if (cancel.IsCancellationRequested ||
                    _disposed ||
                    !_nodes.TryGetValue(entry.Id, out var current) ||
                    current.Source == null)
                {
                    TryDelete(path);
                    return;
                }

                if (!started)
                {
                    current.Source.Stop();
                    current.Source.SetTtsAudio(path);
                    current.Source.Start();
                    current.TtsAudioPath = path;
                    current.TtsStatus = "Speaking";
                    started = true;
                    PublishGraph();
                    PublishTelemetry(force: true);
                }
                else
                {
                    // Keep one sentence buffered: the host may synthesize ahead,
                    // but playback only receives the next file once the previous
                    // one is consumed.
                    while (current.Source.TtsQueuedFiles > 0)
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        await Task.Delay(20, cancel.Token);
                    }

                    current.Source.EnqueueTtsAudio(path);
                    current.TtsAudioPath = path;
                }
            }

            if (!started)
            {
                throw new InvalidOperationException("The text-to-speech model returned no audio.");
            }

            if (_nodes.TryGetValue(entry.Id, out var done) && done.Source != null)
            {
                done.Source.CompleteTtsAudio();
                done.TtsGenerating = false;
                PublishGraph();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_nodes.TryGetValue(entry.Id, out var current))
            {
                current.TtsGenerating = false;
                current.TtsStatus = "Error: " + ex.Message;
            }

            PublishGraph();
            SetStatus("Text-to-speech failed: " + ex.Message, isError: true);
        }
        finally
        {
            bool current = ReferenceEquals(entry.TtsGenerationCancel, cancel);
            if (current)
            {
                entry.TtsGenerationCancel = null;
            }

            if (current && cancel.IsCancellationRequested && _nodes.TryGetValue(entry.Id, out var stopped))
            {
                stopped.TtsGenerating = false;
                stopped.Source?.Stop();
                PublishGraph();
            }

            cancel.Dispose();
        }
    }

    private sealed record TtsSourceState(string Text);

    private void PollTtsSources()
    {
        bool publishReadyStatus = false;
        foreach (var entry in _nodes.Values.Where(entry => entry.Source?.Kind == SourceKind.Tts).ToList())
        {
            if (entry.Source == null)
            {
                continue;
            }

            if (IsBargeInHeld(entry)) continue;

            if (!entry.Source.IsRunning && string.Equals(entry.TtsStatus, "Speaking", StringComparison.OrdinalIgnoreCase))
            {
                entry.TtsStatus = "Ready";
                publishReadyStatus = true;
            }

            if (entry.TtsGenerating || entry.Source.IsRunning ||
                string.IsNullOrWhiteSpace(entry.TtsSourceNodeId))
            {
                continue;
            }

            var source = TextFromSource(entry.TtsSourceNodeId);
            if (source == null)
            {
                continue;
            }

            string text = source.Text;
            if (text.Length < entry.TtsSpokenLength)
            {
                entry.TtsSpokenLength = 0;
            }

            if (text.Length <= entry.TtsSpokenLength)
            {
                continue;
            }

            string suffix = text[entry.TtsSpokenLength..].Trim();
            entry.TtsSpokenLength = text.Length;
            entry.TtsText = text;
            if (suffix.Length >= 2)
            {
                _ = SpeakTtsAsync(entry, suffix, entry.TtsSpeed);
            }
        }

        if (publishReadyStatus)
        {
            PublishGraph();
        }
    }

    private TtsSourceState? TextFromSource(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var producer))
        {
            return null;
        }

        if (producer.Source is { Kind: SourceKind.Network } &&
            producer.Output is { IsNetworkOutput: true })
        {
            return new TtsSourceState(NetworkIncomingText(producer.Id));
        }

        if (producer.Kind == "text")
        {
            return new TtsSourceState(producer.TextCommittedContent);
        }

        if (producer.Modifier == null)
        {
            return null;
        }

        if (producer.TranslatorText != null)
        {
            return new TtsSourceState(producer.TranslatorText.Snapshot().TranslatedText);
        }

        if (producer.Modifier.Processors.OfType<TranscriptionProcessor>().FirstOrDefault() is { } transcriber)
        {
            var snapshot = TranscriptionRegistry.Snapshot(transcriber.TapId);
            if (snapshot == null) return null;
            return new TtsSourceState(snapshot.Transcript);
        }

        if (producer.Modifier.Processors.OfType<TranslatorProcessor>().FirstOrDefault() is { } translator)
        {
            var snapshot = TranslationRegistry.Snapshot(translator.TapId);
            if (snapshot == null) return null;
            return new TtsSourceState(snapshot.TranslatedText);
        }

        return null;
    }

    private List<TtsTextSourceDto> BuildTtsSources(string? excludeId)
    {
        return _nodes.Values
            .Where(entry => !string.Equals(entry.Id, excludeId, StringComparison.Ordinal) &&
                            (entry.Source is { Kind: SourceKind.Network } &&
                             entry.Output is { IsNetworkOutput: true } ||
                             entry.Kind == "text" ||
                             entry.Modifier?.Processors.Any(processor =>
                                 processor is TranscriptionProcessor or TranslatorProcessor) == true))
            .Select(entry => new TtsTextSourceDto
            {
                Id = entry.Id,
                Title = entry.Title,
                Kind = entry.Kind == "text"
                    ? "note"
                    : entry.Source is { Kind: SourceKind.Network }
                        ? "network"
                        : entry.Modifier!.Processors.OfType<TranslatorProcessor>().Any()
                            ? "translator"
                            : "transcriber",
            })
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnSpeechStarted(int tapId)
    {
        if (_disposed) return;
        try
        {
            _window.Dispatcher.BeginInvoke(() => HandleBargeIn(tapId), DispatcherPriority.Background);
        }
        catch
        {
        }
    }

    private void OnSpeechEnded(int tapId)
    {
        if (_disposed) return;
        try
        {
            _window.Dispatcher.BeginInvoke(() => _speechTaps.Remove(tapId), DispatcherPriority.Background);
        }
        catch
        {
        }
    }

    private void HandleBargeIn(int tapId)
    {
        if (_disposed) return;
        _speechTaps.Add(tapId);
        long now = Environment.TickCount64;
        if (now - _lastBargeInTick < 250) return;
        _lastBargeInTick = now;

        foreach (var entry in _nodes.Values.Where(entry => entry.Source?.Kind == SourceKind.Tts).ToList())
        {
            if (IsBargeKeyLinked(entry, tapId) &&
                (entry.TtsGenerating || entry.Source?.IsRunning == true))
            {
                StopTts(entry.Id);
            }
        }
    }

    /// <summary>
    /// Barge-in is opt-in: only a transcriber/translator connected to the TTS
    /// key input may stop it. No key cable means no automatic interruption.
    /// </summary>
    private bool IsBargeInHeld(NodeEntry tts) =>
        _speechTaps.Any(tapId => IsBargeKeyLinked(tts, tapId));

    private bool IsBargeKeyLinked(NodeEntry tts, int tapId)
    {
        NodeEntry? producer = _nodes.Values.FirstOrDefault(entry =>
            entry.Modifier?.Processors.Any(processor =>
                processor is TranscriptionProcessor transcriber && transcriber.TapId == tapId ||
                processor is TranslatorProcessor translator && translator.TapId == tapId) == true);
        return producer != null &&
               !string.Equals(tts.TtsSourceNodeId, producer.Id, StringComparison.Ordinal) &&
               _links.Any(link => link.Enabled &&
                   link.Kind == "barge-key" &&
                   link.From == producer.Id &&
                   link.To == tts.Id);
    }

    private async void DownloadVoskModel()
    {
        if (_voskDownloadCancel != null)
        {
            SetStatus("The Vosk model is already downloading.", isError: true);
            return;
        }

        var cancel = new CancellationTokenSource();
        _voskDownloadCancel = cancel;
        var progress = new Progress<VoskModelProgress>(PublishVoskProgress);
        try
        {
            await VoskModels.DownloadAsync(progress, cancel.Token);
            SetStatus("Vosk model installed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Vosk model download cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Vosk model download failed: " + ex.Message, isError: true);
        }
        finally
        {
            _voskDownloadCancel = null;
            cancel.Dispose();
            PublishSettings();
        }
    }

    private void PublishVoskProgress(VoskModelProgress progress)
    {
        _voskDownload = progress;
        long now = Environment.TickCount64;
        bool terminal = progress.Phase is "done" or "error" or "cancelled";
        if (!terminal && now - _lastVoskPublish < 100) return;
        _lastVoskPublish = now;
        PublishSettings();
    }

    private async void DeleteVoskModel()
    {
        try
        {
            _voskDownloadCancel?.Cancel();
            await VoskModels.DeleteAsync().ConfigureAwait(true);
            PublishSettings();
            SetStatus("Vosk model deleted.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not delete the Vosk model: " + ex.Message, isError: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
