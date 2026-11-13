using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ruse.App;

internal static class TranslationMemory
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _entries;
    private static bool _dirty;
    private static bool _saving;

    private static string FilePath => Path.Combine(AppSettings.Directory, "translation-memory.json");

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
            _entries[key] = translated.Trim();
            _dirty = true;
        }

        SaveSoon();
    }

    private static void LoadLocked()
    {
        if (_entries != null) return;
        try
        {
            if (File.Exists(FilePath))
            {
                _entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else
            {
                _entries = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        catch
        {
            _entries = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void SaveSoon()
    {
        lock (Lock)
        {
            if (_saving) return;
            _saving = true;
            _dirty = false;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Dictionary<string, string> snapshot;
                lock (Lock)
                {
                    LoadLocked();
                    snapshot = new Dictionary<string, string>(_entries!);
                }

                Directory.CreateDirectory(AppSettings.Directory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
            }
            catch
            {
            }
            finally
            {
                lock (Lock)
                {
                    _saving = false;
                    if (_dirty) SaveSoon();
                }
            }
        });
    }

    private static string Key(string provider, string model, string target, string? sourceLanguage, string text)
    {
        string raw = $"{provider}\n{model}\n{target}\n{sourceLanguage ?? ""}\n{text.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
