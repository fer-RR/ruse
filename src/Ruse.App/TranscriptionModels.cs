using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record TranscriberModel(
    string Id,
    string Language,
    string Name,
    int SizeMb,
    string Url);

internal static class TranscriptionModels
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new();

    public static IReadOnlyList<TranscriberModel> All { get; } = new[]
    {
        new TranscriberModel("vosk-model-small-en-us-0.15", "English (US)", "Small English", 39, UrlFor("vosk-model-small-en-us-0.15")),
        new TranscriberModel("vosk-model-small-en-in-0.4", "English (India)", "Small English India", 36, UrlFor("vosk-model-small-en-in-0.4")),
        new TranscriberModel("vosk-model-small-es-0.42", "Spanish", "Small Spanish", 38, UrlFor("vosk-model-small-es-0.42")),
        new TranscriberModel("vosk-model-small-fr-0.22", "French", "Small French", 40, UrlFor("vosk-model-small-fr-0.22")),
        new TranscriberModel("vosk-model-small-de-0.15", "German", "Small German", 44, UrlFor("vosk-model-small-de-0.15")),
        new TranscriberModel("vosk-model-small-pt-0.3", "Portuguese", "Small Portuguese", 31, UrlFor("vosk-model-small-pt-0.3")),
        new TranscriberModel("vosk-model-small-it-0.22", "Italian", "Small Italian", 47, UrlFor("vosk-model-small-it-0.22")),
        new TranscriberModel("vosk-model-small-ru-0.22", "Russian", "Small Russian", 44, UrlFor("vosk-model-small-ru-0.22")),
        new TranscriberModel("vosk-model-small-cn-0.22", "Chinese", "Small Chinese", 42, UrlFor("vosk-model-small-cn-0.22")),
        new TranscriberModel("vosk-model-small-ja-0.22", "Japanese", "Small Japanese", 47, UrlFor("vosk-model-small-ja-0.22")),
        new TranscriberModel("vosk-model-small-ko-0.22", "Korean", "Small Korean", 83, UrlFor("vosk-model-small-ko-0.22")),
        new TranscriberModel("vosk-model-small-nl-0.22", "Dutch", "Small Dutch", 39, UrlFor("vosk-model-small-nl-0.22")),
        new TranscriberModel("vosk-model-small-pl-0.22", "Polish", "Small Polish", 51, UrlFor("vosk-model-small-pl-0.22")),
        new TranscriberModel("vosk-model-small-tr-0.3", "Turkish", "Small Turkish", 35, UrlFor("vosk-model-small-tr-0.3")),
    };

    public static string DefaultId => All[0].Id;

    private static string Root => Path.Combine(AppSettings.Directory, "models");

    public static TranscriberModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsInstalled(string? id) => GetInstalledPath(id) != null;

    public static string? GetInstalledPath(string? id)
    {
        var model = Find(id);
        if (model == null) return null;
        string path = Path.Combine(Root, model.Id);
        return Directory.Exists(path) ? path : null;
    }

    public static string Resolve(string? requested)
    {
        if (IsInstalled(requested)) return requested!;
        return All.FirstOrDefault(model => IsInstalled(model.Id))?.Id ?? "";
    }

    public static async Task DownloadAsync(string id, Action<string> status, CancellationToken token)
    {
        var model = Find(id) ?? throw new InvalidOperationException("Unknown speech model.");
        if (IsInstalled(id)) return;

        await Gate.WaitAsync(token);
        try
        {
            if (IsInstalled(id)) return;
            Directory.CreateDirectory(Root);
            string zip = Path.Combine(Path.GetTempPath(), "ruse-" + model.Id + ".zip");

            status($"Downloading {model.Name}");
            using (var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    if (total > 0) status($"Downloading {model.Name} {copied * 100 / total}%");
                }
            }

            status($"Installing {model.Name}");
            string path = Path.Combine(Root, model.Id);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            ZipFile.ExtractToDirectory(zip, Root);
            File.Delete(zip);
            status($"{model.Name} ready");
        }
        catch (Exception ex)
        {
            status($"{model.Name} failed: {ex.Message}");
            AppLog.Error($"Could not install speech model '{model.Id}'.", ex);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string UrlFor(string id) =>
        "https://alphacephei.com/vosk/models/" + id + ".zip";
}
