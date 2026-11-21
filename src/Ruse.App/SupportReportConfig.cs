using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruse.App;

internal static class SupportReportConfig
{
    // For release builds, fill these with the deployed Apps Script values.
    // For local testing, %APPDATA%\Ruse\support-report.json can override them:
    // { "url": "https://script.google.com/macros/s/.../exec", "token": "..." }
    public const string DefaultUrl = "";
    public const string DefaultToken = "";

    public static string ResolveUrl() => First(DefaultUrl, ReadFile().Url);

    public static string ResolveToken() => First(DefaultToken, ReadFile().Token);

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static (string Url, string Token) ReadFile()
    {
        try
        {
            string path = Path.Combine(AppSettings.Directory, "support-report.json");
            if (!File.Exists(path))
            {
                return ("", "");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string url = root.TryGetProperty("url", out var urlProperty) &&
                         urlProperty.ValueKind == JsonValueKind.String
                ? urlProperty.GetString() ?? ""
                : "";
            string token = root.TryGetProperty("token", out var tokenProperty) &&
                           tokenProperty.ValueKind == JsonValueKind.String
                ? tokenProperty.GetString() ?? ""
                : "";
            return (url, token);
        }
        catch
        {
            return ("", "");
        }
    }
}
