using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void SetTranslatorTextSource(string id, string sourceNodeId)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() != true)
        {
            return;
        }

        string? sourceId = string.IsNullOrWhiteSpace(sourceNodeId) ? null : sourceNodeId.Trim();
        if (sourceId != null &&
            (!_nodes.TryGetValue(sourceId, out var source) ||
             !IsTextSourceNode(source)))
        {
            SetStatus("Choose a network link or text note as the translator's text source.", isError: true);
            return;
        }

        entry.TranslatorTextSourceId = sourceId;
        RestartTranslatorText(entry);
        PublishGraph();
        PublishTelemetry(force: true);
        SetStatus(sourceId == null
            ? $"{entry.Title}: translating audio."
            : $"{entry.Title}: translating incoming text from {_nodes[sourceId].Title}.");
    }

    private void RestartTranslatorText(NodeEntry entry)
    {
        entry.TranslatorText?.Dispose();
        entry.TranslatorText = null;
        if (string.IsNullOrWhiteSpace(entry.TranslatorTextSourceId) ||
            !_nodes.TryGetValue(entry.TranslatorTextSourceId, out var source) ||
            !IsTextSourceNode(source))
        {
            entry.TranslatorTextSourceId = null;
            return;
        }

        entry.TranslatorText = new NetworkTextTranslation(
            entry.TranslatorModel,
            entry.TranslatorTarget ?? TranslationLanguages.DefaultCode,
            _settings.TranslationProvider);
        if (source.Kind == "text")
        {
            entry.TranslatorText.Submit(source.TextCommittedContent);
        }
    }

    private static bool IsTextSourceNode(NodeEntry entry) =>
        entry.Kind == "text" ||
        entry.Source is { Kind: SourceKind.Network } &&
        entry.Output is { IsNetworkOutput: true };

    private List<TtsTextSourceDto> BuildNetworkTextSources(string? excludeId) =>
        _nodes.Values
            .Where(entry => !string.Equals(entry.Id, excludeId, StringComparison.Ordinal) &&
                            IsTextSourceNode(entry))
            .Select(entry => new TtsTextSourceDto
            {
                Id = entry.Id,
                Title = entry.Title,
                Kind = entry.Kind == "text" ? "note" : "network",
            })
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
