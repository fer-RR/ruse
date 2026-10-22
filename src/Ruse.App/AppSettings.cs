using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruse.App;

/// <summary>
/// Small user-level settings file (window bounds, last session). Stored under
/// %APPDATA%\Ruse\settings.json so it survives rebuilds of the exe.
/// </summary>
public sealed class AppSettings
{
    public double Width { get; set; } = 1345;
    public double Height { get; set; } = 762;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool Maximized { get; set; }
    public bool AutoLoadSession { get; set; } = true;

    /// <summary>Draw a red border around muted nodes so they stand out.</summary>
    public bool HighlightMutedNodes { get; set; } = true;

    /// <summary>When the mini-map is shown (0 = always, 1 = never, 2 = when zoomed in).</summary>
    public int MinimapMode { get; set; } = 2;

    /// <summary>
    /// Where settings and the last session live. Self-tests override this so they
    /// never read or overwrite the user's real data.
    /// </summary>
    [JsonIgnore]
    public static string Directory =>
        OverrideDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ruse");

    /// <summary>Redirects settings/session storage (used by the UI self-test).</summary>
    [JsonIgnore]
    public static string? OverrideDirectory { get; set; }

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    [JsonIgnore]
    public static string SessionPath => Path.Combine(Directory, "session.ruse.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
        }
    }
}
