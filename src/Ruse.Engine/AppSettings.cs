using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruse.App;

/// <summary>User settings stored in %APPDATA%\Ruse\settings.json.</summary>
public sealed class AppSettings
{
    public double Width { get; set; } = 1345;
    public double Height { get; set; } = 762;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool Maximized { get; set; }
    public bool AutoLoadSession { get; set; } = true;
    public bool OnboardingCompleted { get; set; }
    public bool ShowWelcomeScreen { get; set; } = true;

    /// <summary>Hide the window to the notification area when minimized.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool FollowPlayingApps { get; set; }

    /// <summary>Output device used when auto-adding a newly playing app.</summary>
    public string FollowOutputId { get; set; } = "";

    /// <summary>UI language code; the engine and audio status stay English for now.</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>Draw a red border around muted nodes so they stand out.</summary>
    public bool HighlightMutedNodes { get; set; } = true;

    /// <summary>Glow nodes and library items while signal is flowing.</summary>
    public bool SignalGlow { get; set; } = true;

    /// <summary>Align dragged nodes to the 20 px canvas grid.</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>Highlight a cable when the pointer is over it.</summary>
    public bool HighlightCableOnHover { get; set; } = true;

    /// <summary>Animate small packets along cables that carry signal.</summary>
    public bool CableAnimation { get; set; } = true;

    /// <summary>Default Whisper model id for new transcriber nodes.</summary>
    public string TranscriberModel { get; set; } = "whisper-small.en";
    public string TranslatorModel { get; set; } = "qwen2.5-1.5b-instruct-q4_k_m";
    public string TranslatorTarget { get; set; } = "en";

    /// <summary>When the mini-map is shown (0 = always, 1 = never, 2 = when zoomed in).</summary>
    public int MinimapMode { get; set; } = 2;

    /// <summary>Show the live engine/UI diagnostics overlay for troubleshooting.</summary>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Start the restored graph automatically once the window is ready.</summary>
    public bool StartRoutingOnLaunch { get; set; }

    /// <summary>Settings/session folder; self-tests redirect it.</summary>
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
