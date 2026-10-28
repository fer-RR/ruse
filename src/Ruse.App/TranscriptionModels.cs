using System.IO;
using System.Net.Http;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record TranscriberModel(
    string Id,
    string Language,
    string Name,
    string Tier,
    int SizeMb,
    string Url,
    string? Note = null);

internal sealed record TranscriberModelProgress(
    string ModelId,
    string Phase,
    int Percent,
    string Message);

internal static class TranscriptionModels
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new();

    public static IReadOnlyList<TranscriberModel> All { get; } = new[]
    {
        Model("whisper-tiny.en", "English", "Whisper Tiny English", "Tiny", 75, "ggml-tiny.en.bin", "OpenAI weights · MIT"),
        Model("whisper-base.en", "English", "Whisper Base English", "Base", 142, "ggml-base.en.bin", "OpenAI weights · MIT"),
        Model("whisper-small.en", "English", "Whisper Small English", "Small", 466, "ggml-small.en.bin", "OpenAI weights · MIT"),
        Model("whisper-medium.en", "English", "Whisper Medium English", "Medium", 1500, "ggml-medium.en.bin", "OpenAI weights · MIT"),
        Model("whisper-tiny", "Multilingual", "Whisper Tiny", "Tiny", 75, "ggml-tiny.bin", "OpenAI weights · MIT"),
        Model("whisper-base", "Multilingual", "Whisper Base", "Base", 142, "ggml-base.bin", "OpenAI weights · MIT"),
        Model("whisper-small", "Multilingual", "Whisper Small", "Small", 466, "ggml-small.bin", "OpenAI weights · MIT"),
        Model("whisper-medium", "Multilingual", "Whisper Medium", "Medium", 1500, "ggml-medium.bin", "OpenAI weights · MIT"),
        Model("whisper-large-v3-turbo", "Multilingual", "Whisper Large v3 Turbo", "Large", 1620, "ggml-large-v3-turbo.bin", "OpenAI weights · MIT"),
        Model("whisper-large-v3", "Multilingual", "Whisper Large v3", "Large", 3090, "ggml-large-v3.bin", "OpenAI weights · MIT"),
    };

    public static string DefaultId => "whisper-small.en";

    public static string Root => Path.Combine(AppSettings.Directory, "models");

    public static TranscriberModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string PathFor(string id) => Path.Combine(Root, id + ".bin");

    public static bool IsInstalled(string? id) => GetInstalledPath(id) != null;

    public static string? GetInstalledPath(string? id)
    {
        var model = Find(id);
        if (model == null) return null;
        string path = PathFor(model.Id);
        return IsValidModelFile(path) ? path : null;
    }

    public static long GetInstalledSizeBytes(string? id)
    {
        string? path = GetInstalledPath(id);
        if (path == null) return 0;
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    public static string Resolve(string? requested)
    {
        if (IsInstalled(requested)) return requested!;
        if (IsInstalled(DefaultId)) return DefaultId;
        return All.FirstOrDefault(model => IsInstalled(model.Id))?.Id ?? "";
    }

    /// <summary>Translators need a multilingual model; English-only models cannot translate.</summary>
    public static string ResolveMultilingual(string? requested)
    {
        if (IsInstalled(requested) && IsMultilingual(requested)) return requested!;
        foreach (string id in new[]
                 {
                     "whisper-small", "whisper-base", "whisper-tiny", "whisper-medium",
                     "whisper-large-v3-turbo", "whisper-large-v3",
                 })
        {
            if (IsInstalled(id)) return id;
        }

        return All.FirstOrDefault(model => model.Language == "Multilingual" && IsInstalled(model.Id))?.Id ?? "";
    }

    public static bool IsMultilingual(string? id) =>
        string.Equals(Find(id)?.Language, "Multilingual", StringComparison.OrdinalIgnoreCase);

    public static async Task DownloadAsync(
        string id,
        IProgress<TranscriberModelProgress>? progress,
        CancellationToken token)
    {
        var model = Find(id) ?? throw new InvalidOperationException("Unknown speech model.");
        if (IsInstalled(id)) return;

        await Gate.WaitAsync(token);
        string temp = PathFor(id) + ".part";
        try
        {
            if (IsInstalled(id)) return;
            Directory.CreateDirectory(Root);

            progress?.Report(new TranscriberModelProgress(id, "downloading", 0, $"Downloading {model.Name}"));
            long total = 0;
            long copied = 0;
            using (var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(temp);
                var buffer = new byte[81920];
                int read;
                int lastPercent = -1;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    if (total > 0)
                    {
                        int percent = (int)Math.Clamp(copied * 100 / total, 0, 99);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new TranscriberModelProgress(
                                id, "downloading", percent,
                                $"Downloading {model.Name} · {percent}%"));
                        }
                    }
                }
            }

            if (total > 0 && copied != total)
            {
                throw new InvalidDataException("The download was incomplete. Try again.");
            }

            if (!IsValidModelFile(temp))
            {
                throw new InvalidDataException("The downloaded file is not a valid Whisper model.");
            }

            progress?.Report(new TranscriberModelProgress(id, "installing", 100, $"Installing {model.Name}"));
            File.Move(temp, PathFor(id), overwrite: true);
            progress?.Report(new TranscriberModelProgress(id, "done", 100, $"{model.Name} ready"));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new TranscriberModelProgress(id, "cancelled", 0, $"{model.Name} download cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new TranscriberModelProgress(id, "error", 0, $"{model.Name} failed: {ex.Message}"));
            AppLog.Error($"Could not install speech model '{id}'.", ex);
            throw;
        }
        finally
        {
            TryDelete(temp);
            Gate.Release();
        }
    }

    public static void Delete(string id) => TryDelete(PathFor(id));

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double megabytes = bytes / (1024.0 * 1024.0);
        return megabytes >= 1024
            ? $"{megabytes / 1024.0:0.0} GB"
            : $"{megabytes:0} MB";
    }

    internal static bool IsValidModelFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 1_000_000;
        }
        catch
        {
            return false;
        }
    }

    private static TranscriberModel Model(string id, string language, string name, string tier, int sizeMb, string file, string note) =>
        new(id, language, name, tier, sizeMb,
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + file, note);

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
}
