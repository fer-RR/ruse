using System.IO;
using System.Net.Http;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record TranslationModel(
    string Id,
    string Name,
    string Tier,
    int SizeMb,
    string Url,
    string License,
    string Note,
    int VramMb = 0);

internal sealed record TranslationModelProgress(
    string ModelId,
    string Phase,
    int Percent,
    string Message);

internal static class TranslationModels
{
    private const long MinimumSize = 50L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateHttpClient();

    public static IReadOnlyList<TranslationModel> All { get; } = new[]
    {
        Model("qwen2.5-0.5b-instruct-q4_k_m", "Qwen2.5 0.5B Instruct", "Fast", 469,
            "Qwen/Qwen2.5-0.5B-Instruct-GGUF", "qwen2.5-0.5b-instruct-q4_k_m.gguf", "Apache-2.0",
            "Low memory, good for short and simple sentences.", vramMb: 2048),
        Model("qwen2.5-1.5b-instruct-q4_k_m", "Qwen2.5 1.5B Instruct", "Balanced", 1066,
            "Qwen/Qwen2.5-1.5B-Instruct-GGUF", "qwen2.5-1.5b-instruct-q4_k_m.gguf", "Apache-2.0",
            "Recommended balance of quality, memory and speed.", vramMb: 4096),
        Model("qwen2.5-3b-instruct-q4_k_m", "Qwen2.5 3B Instruct", "Quality", 2007,
            "Qwen/Qwen2.5-3B-Instruct-GGUF", "qwen2.5-3b-instruct-q4_k_m.gguf", "Apache-2.0",
            "Better wording and context, at the cost of memory and speed.", vramMb: 6144),
    };

    public static string DefaultId => "qwen2.5-1.5b-instruct-q4_k_m";

    public static string Root => Path.Combine(AppSettings.Directory, "models", "translation");

    public static TranslationModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string PathFor(string id) => Path.Combine(Root, id + ".gguf");

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

    public static async Task DownloadAsync(
        string id,
        IProgress<TranslationModelProgress>? progress,
        CancellationToken token)
    {
        var model = Find(id) ?? throw new InvalidOperationException("Unknown translation model.");
        if (IsInstalled(id)) return;

        await Gate.WaitAsync(token);
        string temp = PathFor(id) + ".part";
        try
        {
            if (IsInstalled(id)) return;
            Directory.CreateDirectory(Root);

            progress?.Report(new TranslationModelProgress(id, "downloading", 0, $"Downloading {model.Name}"));
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
                            progress?.Report(new TranslationModelProgress(
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
                throw new InvalidDataException("The downloaded file is not a valid GGUF model.");
            }

            progress?.Report(new TranslationModelProgress(id, "installing", 100, $"Installing {model.Name}"));
            File.Move(temp, PathFor(id), overwrite: true);
            progress?.Report(new TranslationModelProgress(id, "done", 100, $"{model.Name} ready"));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new TranslationModelProgress(id, "cancelled", 0, $"{model.Name} download cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new TranslationModelProgress(id, "error", 0, $"{model.Name} failed: {ex.Message}"));
            AppLog.Error($"Could not install translation model '{id}'.", ex);
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
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinimumSize)
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            using var stream = File.OpenRead(path);
            return stream.Read(magic) == 4 &&
                   magic[0] == (byte)'G' &&
                   magic[1] == (byte)'G' &&
                   magic[2] == (byte)'U' &&
                   magic[3] == (byte)'F';
        }
        catch
        {
            return false;
        }
    }

    private static TranslationModel Model(
        string id,
        string name,
        string tier,
        int sizeMb,
        string repo,
        string file,
        string license,
        string note,
        int vramMb = 0) =>
        new(id, name, tier, sizeMb,
            $"https://huggingface.co/{repo}/resolve/main/{file}",
            license, note, vramMb);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ruse/1.0");
        return client;
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
}
