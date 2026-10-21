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

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ruse");

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
