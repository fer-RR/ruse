using System.IO;
using System.Text.Json;

namespace Ruse.App;

internal static class FrozenProxyStore
{
    private sealed class State
    {
        public List<string> Tracked { get; set; } = new();
        public Dictionary<string, string> Trash { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object Sync = new();
    private static readonly HashSet<string> Tracked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Trashed = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static string ManifestPath => Path.Combine(AppSettings.Directory, "frozen-proxies.json");
    private static string TrashDirectory => Path.Combine(AppSettings.Directory, "frozen-trash");

    public static void Initialize() => EnsureLoaded();

    public static bool IsTracked(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        EnsureLoaded();
        lock (Sync)
        {
            return Tracked.Contains(Normalize(path));
        }
    }

    public static void Track(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        EnsureLoaded();
        lock (Sync)
        {
            string fullPath = Normalize(path);
            if (!File.Exists(fullPath)) return;
            Tracked.Add(fullPath);
            SaveLocked();
        }
    }

    public static void Forget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        EnsureLoaded();
        lock (Sync)
        {
            string fullPath = Normalize(path);
            Tracked.Remove(fullPath);
            Trashed.Remove(fullPath);
            SaveLocked();
        }
    }

    public static bool MoveToTrash(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        EnsureLoaded();
        lock (Sync)
        {
            string fullPath = Normalize(path);
            if (!Tracked.Contains(fullPath) || !File.Exists(fullPath)) return false;
            if (Trashed.ContainsKey(fullPath)) return true;

            try
            {
                Directory.CreateDirectory(TrashDirectory);
                string trashPath = Path.Combine(
                    TrashDirectory,
                    Guid.NewGuid().ToString("N") + Path.GetExtension(fullPath));
                File.Move(fullPath, trashPath);
                Trashed[fullPath] = trashPath;
                SaveLocked();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool Restore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        EnsureLoaded();
        lock (Sync)
        {
            string fullPath = Normalize(path);
            if (!Trashed.TryGetValue(fullPath, out string? trashPath)) return false;
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(trashPath);
                }
                else
                {
                    string? directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.Move(trashPath, fullPath);
                }

                Trashed.Remove(fullPath);
                SaveLocked();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<string> TrackedPaths()
    {
        EnsureLoaded();
        lock (Sync)
        {
            return Tracked.ToList();
        }
    }

    public static void CleanupTrash()
    {
        EnsureLoaded();
        lock (Sync)
        {
            try
            {
                if (Directory.Exists(TrashDirectory))
                {
                    foreach (string trashPath in Directory.GetFiles(TrashDirectory))
                    {
                        try { File.Delete(trashPath); } catch { }
                    }
                }
            }
            catch
            {
            }

            Trashed.Clear();
            Tracked.RemoveWhere(path => !File.Exists(path));
            SaveLocked();
        }
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(ManifestPath)) return;
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(ManifestPath));
                if (state == null) return;
                foreach (string path in state.Tracked)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        Tracked.Add(Normalize(path));
                    }
                }

                foreach (var pair in state.Trash)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) ||
                        string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    Trashed[Normalize(pair.Key)] = pair.Value;
                }

                RestoreAllTrashLocked();
            }
            catch
            {
                Tracked.Clear();
                Trashed.Clear();
            }
        }
    }

    private static void RestoreAllTrashLocked()
    {
        bool changed = false;
        foreach (var pair in Trashed.ToList())
        {
            try
            {
                if (!File.Exists(pair.Value)) continue;
                if (File.Exists(pair.Key))
                {
                    File.Delete(pair.Value);
                }
                else
                {
                    string? directory = Path.GetDirectoryName(pair.Key);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.Move(pair.Value, pair.Key);
                }

                Trashed.Remove(pair.Key);
                changed = true;
            }
            catch
            {
            }
        }

        if (changed) SaveLocked();
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            var state = new State
            {
                Tracked = Tracked.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                Trash = new Dictionary<string, string>(Trashed, StringComparer.OrdinalIgnoreCase),
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(state));
        }
        catch
        {
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
