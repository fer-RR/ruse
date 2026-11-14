using System.IO;
using System.Text.Json;

namespace Ruse.App;

/// <summary>
/// Remembers the outgoing cables of an application source after the user
/// removes that source. Re-adding the same app can reconnect those cables when
/// the setting is enabled.
/// </summary>
internal static class AppRouteMemory
{
    private const int MaxApps = 100;
    private const int MaxRoutesPerApp = 32;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static Dictionary<string, List<SavedRoute>>? _entries;

    private static string FilePath => Path.Combine(AppSettings.Directory, "app-routes.json");

    internal sealed class SavedRoute
    {
        public string TargetControlId { get; set; } = "";
        public string TargetTitle { get; set; } = "";
        public string Kind { get; set; } = "direct";
        public float Gain { get; set; } = 1f;
        public bool Native { get; set; }
    }

    public static void Remember(string? processName, IReadOnlyList<SavedRoute> routes)
    {
        if (string.IsNullOrWhiteSpace(processName) || routes.Count == 0) return;

        lock (Lock)
        {
            LoadLocked();
            string key = processName.Trim();
            if (!_entries!.ContainsKey(key) && _entries.Count >= MaxApps) return;
            _entries[key] = routes.Take(MaxRoutesPerApp).ToList();
            SaveLocked();
        }
    }

    public static IReadOnlyList<SavedRoute> Load(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return Array.Empty<SavedRoute>();
        lock (Lock)
        {
            LoadLocked();
            return _entries!.TryGetValue(processName.Trim(), out var routes)
                ? routes.ToArray()
                : Array.Empty<SavedRoute>();
        }
    }

    private static void LoadLocked()
    {
        if (_entries != null) return;
        try
        {
            var loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<SavedRoute>>>(File.ReadAllText(FilePath))
                : null;
            _entries = loaded == null
                ? new Dictionary<string, List<SavedRoute>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<SavedRoute>>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _entries = new Dictionary<string, List<SavedRoute>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
        }
    }
}
