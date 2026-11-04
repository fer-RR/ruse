using System.Diagnostics;
using System.IO;
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

    private void DeleteTtsModel(string modelId)
    {
        if (!TtsModels.IsInstalled(modelId))
        {
            return;
        }

        try
        {
            TtsModels.Delete(modelId);
            if (string.Equals(_settings.TtsModel, modelId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.TtsModel = TtsModels.DefaultId;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry => entry.Source?.Kind == SourceKind.Tts &&
                         string.Equals(entry.TtsModel, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                entry.TtsStatus = "No model installed";
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
        entry.TtsSpokenLength = 0;
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
            string text = File.ReadAllText(dialog.FileName);
            if (text.Length > 20000)
            {
                text = text[..20000];
            }

            entry.TtsText = text;
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

    private async Task SpeakTtsAsync(NodeEntry entry, string text)
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

        var sentences = SentenceSplitter.Split(text, 0, 1)
            .Select(cue => cue.Text.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();
        if (sentences.Count == 0)
        {
            sentences.Add(text.Trim());
        }

        var cancel = new CancellationTokenSource();
        entry.TtsGenerationCancel?.Cancel();
        entry.TtsGenerationCancel = cancel;
        entry.TtsGenerating = true;
        entry.TtsStatus = "Generating";
        entry.TtsModel = modelId;
        PublishGraph();
        try
        {
            string firstPath = await TtsSynthesis.SynthesizeAsync(
                modelId,
                sentences[0],
                entry.TtsSpeed,
                entry.TtsVoice,
                cancel.Token);
            if (_disposed || !_nodes.TryGetValue(entry.Id, out var current) || current.Source == null)
            {
                TryDelete(firstPath);
                return;
            }

            current.Source.Stop();
            current.Source.SetTtsAudio(firstPath);
            current.Source.Start();
            current.TtsAudioPath = firstPath;
            current.TtsStatus = "Speaking";
            PublishGraph();
            PublishTelemetry(force: true);

            for (int index = 1; index < sentences.Count; index++)
            {
                // Keep one sentence buffered: synthesize the next sentence while
                // the previous one is playing, but do not run the whole text
                // ahead of the playback queue.
                while (current.Source.TtsQueuedFiles > 0)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    await Task.Delay(20, cancel.Token);
                }

                cancel.Token.ThrowIfCancellationRequested();
                string path = await TtsSynthesis.SynthesizeAsync(
                    modelId,
                    sentences[index],
                    entry.TtsSpeed,
                    entry.TtsVoice,
                    cancel.Token);
                if (cancel.IsCancellationRequested ||
                    _disposed ||
                    !_nodes.TryGetValue(entry.Id, out current) ||
                    current.Source == null)
                {
                    TryDelete(path);
                    return;
                }

                current.Source.EnqueueTtsAudio(path);
                current.TtsAudioPath = path;
            }

            current.Source.CompleteTtsAudio();
            current.TtsGenerating = false;
            PublishGraph();
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
            if (ReferenceEquals(entry.TtsGenerationCancel, cancel))
            {
                entry.TtsGenerationCancel = null;
            }

            cancel.Dispose();
        }
    }

    private void PollTtsSources()
    {
        foreach (var entry in _nodes.Values.Where(entry => entry.Source?.Kind == SourceKind.Tts).ToList())
        {
            if (entry.Source == null)
            {
                continue;
            }

            if (!entry.Source.IsRunning && string.Equals(entry.TtsStatus, "Speaking", StringComparison.OrdinalIgnoreCase))
            {
                entry.TtsStatus = "Ready";
            }

            if (entry.TtsGenerating || entry.Source.IsRunning ||
                string.IsNullOrWhiteSpace(entry.TtsSourceNodeId))
            {
                continue;
            }

            string? text = TextFromSource(entry.TtsSourceNodeId);
            if (text == null)
            {
                continue;
            }

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
                _ = SpeakTtsAsync(entry, suffix);
            }
        }
    }

    private string? TextFromSource(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var producer) || producer.Modifier == null)
        {
            return null;
        }

        if (producer.Modifier.Processors.OfType<TranscriptionProcessor>().FirstOrDefault() is { } transcriber)
        {
            return TranscriptionRegistry.Snapshot(transcriber.TapId)?.Transcript ?? "";
        }

        if (producer.Modifier.Processors.OfType<TranslatorProcessor>().FirstOrDefault() is { } translator)
        {
            return TranslationRegistry.Snapshot(translator.TapId)?.TranslatedText ?? "";
        }

        return null;
    }

    private List<TtsTextSourceDto> BuildTtsSources(string? excludeId)
    {
        return _nodes.Values
            .Where(entry => !string.Equals(entry.Id, excludeId, StringComparison.Ordinal) &&
                            entry.Modifier?.Processors.Any(processor =>
                                processor is TranscriptionProcessor or TranslatorProcessor) == true)
            .Select(entry => new TtsTextSourceDto
            {
                Id = entry.Id,
                Title = entry.Title,
                Kind = entry.Modifier!.Processors.OfType<TranslatorProcessor>().Any() ? "translator" : "transcriber",
            })
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
