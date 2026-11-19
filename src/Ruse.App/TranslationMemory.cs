using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ruse.App;

internal static class TranslationMemory
{
    private const int CompactAfterEntries = 2000;
    private static readonly object Lock = new();
    private static readonly object WriteLock = new();
    private static readonly Dictionary<string, string> Pending = new(StringComparer.Ordinal);
    private static Dictionary<string, string>? _entries;
    private static bool _dirty;
    private static bool _saving;
    private static bool _saveScheduled;
    private static int _journalEntries;

    private static string FilePath => Path.Combine(AppSettings.Directory, "translation-memory.json");
    private static string JournalPath => Path.Combine(AppSettings.Directory, "translation-memory.log");

    public static string? TryGet(string provider, string model, string target, string? sourceLanguage, string text)
    {
        if (!TranslationSettings.MemoryEnabled || string.IsNullOrWhiteSpace(text)) return null;
        lock (Lock)
        {
            LoadLocked();
            return _entries!.TryGetValue(Key(provider, model, target, sourceLanguage, text), out string? value)
                ? value : null;
        }
    }

    public static void Store(string provider, string model, string target, string? sourceLanguage, string source, string translated)
    {
        if (!TranslationSettings.MemoryEnabled ||
            source.Length > 4000 || translated.Length > 4000 ||
            string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated) ||
            string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        lock (Lock)
        {
            LoadLocked();
            if (_entries!.Count >= 20000) return;
            string key = Key(provider, model, target, sourceLanguage, source);
            if (_entries.ContainsKey(key)) return;
            string value = translated.Trim();
            _entries[key] = value;
            Pending[key] = value;
            _dirty = true;
        }

        SaveSoon();
    }

    /// <summary>Flushes pending entries and compacts the journal for shutdown.</summary>
    public static void Flush()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dictionary<string, string> snapshot;
            lock (Lock)
            {
                LoadLocked();
                snapshot = new Dictionary<string, string>(_entries!);
                Pending.Clear();
                _dirty = false;
            }

            WriteBase(snapshot);
            lock (Lock)
            {
                if (!_dirty && Pending.Count == 0)
                {
                    return;
                }
            }
        }
    }

    private static void LoadLocked()
    {
        if (_entries != null) return;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    foreach (var pair in loaded) entries[pair.Key] = pair.Value;
                }
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists(JournalPath))
            {
                foreach (string line in File.ReadLines(JournalPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                        if (entry != null && !string.IsNullOrWhiteSpace(entry.Key))
                        {
                            entries[entry.Key] = entry.Value;
                            _journalEntries++;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        _entries = entries;
    }

    private static void SaveSoon()
    {
        lock (Lock)
        {
            if (_saving || _saveScheduled) return;
            _saveScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            Dictionary<string, string>? pending = null;
            bool failed = false;
            try
            {
                await Task.Delay(750);
                lock (Lock)
                {
                    _saveScheduled = false;
                    _saving = true;
                    LoadLocked();
                    pending = new Dictionary<string, string>(Pending, StringComparer.Ordinal);
                    Pending.Clear();
                }

                if (pending.Count > 0)
                {
                    AppendJournal(pending);
                    pending = null;
                }

                if (_journalEntries >= CompactAfterEntries)
                {
                    Compact();
                }
            }
            catch
            {
                failed = true;
                if (pending != null)
                {
                    lock (Lock)
                    {
                        foreach (var pair in pending) Pending[pair.Key] = pair.Value;
                        _dirty = true;
                    }
                }
            }
            finally
            {
                lock (Lock)
                {
                    _saveScheduled = false;
                    _saving = false;
                    _dirty = Pending.Count > 0;
                    if (_dirty && !failed) SaveSoon();
                }
            }
        });
    }

    private static void AppendJournal(Dictionary<string, string> pending)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(AppSettings.Directory);
            var lines = new string[pending.Count];
            int index = 0;
            foreach (var pair in pending)
            {
                lines[index++] = JsonSerializer.Serialize(new JournalEntry(pair.Key, pair.Value));
            }

            File.AppendAllLines(JournalPath, lines, new UTF8Encoding(false));
            _journalEntries += lines.Length;
        }
    }

    private static void Compact()
    {
        Dictionary<string, string> snapshot;
        lock (Lock)
        {
            LoadLocked();
            snapshot = new Dictionary<string, string>(_entries!);
        }

        WriteBase(snapshot);
    }

    private static void WriteBase(Dictionary<string, string> snapshot)
    {
        lock (WriteLock)
        {
            string temp = FilePath + ".tmp";
            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, FilePath, overwrite: true);
            TryDelete(JournalPath);
            _journalEntries = 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static string Key(string provider, string model, string target, string? sourceLanguage, string text)
    {
        string raw = $"{provider}\n{model}\n{target}\n{sourceLanguage ?? ""}\n{text.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private sealed record JournalEntry(string Key, string Value);
}
