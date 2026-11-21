using System.IO;
using System.Text;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private const int MaxTextNoteCharacters = 200_000;

    private void SetTextNoteContent(string id, string text)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Kind != "text")
        {
            return;
        }

        entry.TextContent = NormalizeTextNote(text);
        PublishGraph();
    }

    private void CommitTextNote(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Kind != "text" ||
            string.Equals(entry.TextContent, entry.TextCommittedContent, StringComparison.Ordinal))
        {
            return;
        }

        entry.TextCommittedContent = entry.TextContent;
        NotifyTextNoteConsumers(id);
    }

    private void SaveTextNote(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Kind != "text")
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save text note",
            Filter = "Text files|*.txt|Markdown files|*.md|All files|*.*",
            FileName = string.IsNullOrWhiteSpace(entry.TextFileName)
                ? $"{SafeFileName(entry.Title)}.txt"
                : SafeFileName(Path.GetFileName(entry.TextFileName)),
            InitialDirectory = RuseFolders.Ensure(RuseFolders.Notes),
            AddExtension = true,
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            if (!AtomicFile.TryWriteAllText(dialog.FileName, entry.TextContent, out string error))
            {
                throw new IOException(error);
            }
            entry.TextFileName = Path.GetFileName(dialog.FileName);
            entry.Title = Path.GetFileNameWithoutExtension(dialog.FileName);
            PublishGraph();
            SetStatus($"{entry.Title}: text note saved.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not save the text note: " + ex.Message, isError: true);
        }
    }

    private void LoadTextNote(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Kind != "text")
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load text note",
            Filter = "Text files|*.txt;*.md;*.srt;*.vtt|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = RuseFolders.Ensure(RuseFolders.Notes),
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            string text = ReadTextNoteFile(dialog.FileName);
            entry.TextContent = NormalizeTextNote(text);
            entry.TextCommittedContent = entry.TextContent;
            entry.TextFileName = Path.GetFileName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(entry.Title) || entry.Title == "Text note")
            {
                entry.Title = Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            PublishGraph();
            NotifyTextNoteConsumers(id);
            SetStatus($"{entry.Title}: text note loaded.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not load the text note: " + ex.Message, isError: true);
        }
    }

    private void NotifyTextNoteConsumers(string noteId)
    {
        foreach (var entry in _nodes.Values)
        {
            if (string.Equals(entry.TranslatorTextSourceId, noteId, StringComparison.Ordinal))
            {
                RestartTranslatorText(entry);
            }
        }
    }

    private static string ReadTextNoteFile(string path)
    {
        using var reader = new StreamReader(
            path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        var buffer = new char[8192];
        while (text.Length < MaxTextNoteCharacters)
        {
            int read = reader.Read(
                buffer, 0, Math.Min(buffer.Length, MaxTextNoteCharacters - text.Length));
            if (read <= 0)
            {
                break;
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    private static string NormalizeTextNote(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        string normalized = text.Replace("\0", "").Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Length <= MaxTextNoteCharacters
            ? normalized
            : normalized[..MaxTextNoteCharacters];
    }

    private static string SafeFileName(string title)
    {
        string name = string.IsNullOrWhiteSpace(title) ? "Text note" : title.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length == 0 ? "Text note" : name;
    }
}
