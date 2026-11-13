using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record TranscriberModel(
    string Id,
    string Language,
    string Name,
    string Tier,
    int SizeMb,
    string Url,
    string? Note = null,
    string? Sha256 = null,
    int VramMb = 0);

internal sealed record TranscriberModelProgress(
    string ModelId,
    string Phase,
    int Percent,
    string Message);

internal static class TranscriptionModels
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new();
    private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["ggml-tiny.en.bin"] = "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
        ["ggml-base.en.bin"] = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
        ["ggml-small.en.bin"] = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d",
        ["ggml-medium.en.bin"] = "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356",
        ["ggml-tiny.bin"] = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
        ["ggml-base.bin"] = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        ["ggml-small.bin"] = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        ["ggml-medium.bin"] = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        ["ggml-large-v3-turbo.bin"] = "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
        ["ggml-large-v3.bin"] = "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
    };

    public static IReadOnlyList<TranscriberModel> All { get; } = new[]
    {
        Model("whisper-tiny.en", "English", "Whisper Tiny English", "Tiny", 75, "ggml-tiny.en.bin", "OpenAI weights · MIT", vramMb: 1024),
        Model("whisper-base.en", "English", "Whisper Base English", "Base", 142, "ggml-base.en.bin", "OpenAI weights · MIT", vramMb: 1024),
        Model("whisper-small.en", "English", "Whisper Small English", "Small", 466, "ggml-small.en.bin", "OpenAI weights · MIT", vramMb: 2048),
        Model("whisper-medium.en", "English", "Whisper Medium English", "Medium", 1500, "ggml-medium.en.bin", "OpenAI weights · MIT", vramMb: 5120),
        Model("whisper-tiny", "Multilingual", "Whisper Tiny", "Tiny", 75, "ggml-tiny.bin", "OpenAI weights · MIT", vramMb: 1024),
        Model("whisper-base", "Multilingual", "Whisper Base", "Base", 142, "ggml-base.bin", "OpenAI weights · MIT", vramMb: 1024),
        Model("whisper-small", "Multilingual", "Whisper Small", "Small", 466, "ggml-small.bin", "OpenAI weights · MIT", vramMb: 2048),
        Model("whisper-medium", "Multilingual", "Whisper Medium", "Medium", 1500, "ggml-medium.bin", "OpenAI weights · MIT", vramMb: 5120),
        Model("whisper-large-v3-turbo", "Multilingual", "Whisper Large v3 Turbo", "Large", 1620, "ggml-large-v3-turbo.bin", "OpenAI weights · MIT", vramMb: 8192),
        Model("whisper-large-v3", "Multilingual", "Whisper Large v3", "Large", 3090, "ggml-large-v3.bin", "OpenAI weights · MIT", vramMb: 10240),
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
        ModelPreflight.Check(Root, model.SizeMb);

        await Gate.WaitAsync(token);
        string temp = PathFor(id) + ".part";
        try
        {
            if (IsInstalled(id)) return;
            Directory.CreateDirectory(Root);

            progress?.Report(new TranscriberModelProgress(id, "downloading", 0, $"Downloading {model.Name}"));
            long total = 0;
            long copied = 0;
            string? expectedHash = null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength ?? 0;
                expectedHash = ModelIntegrity.NormalizeSha256(model.Sha256) ??
                               ModelIntegrity.FromETag(response.Headers.ETag);
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(temp);
                var buffer = new byte[81920];
                int read;
                int lastPercent = -1;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    copied += read;
                    if (total > 0)
                    {
                        int percent = (int)Math.Clamp(copied * 100 / total, 0, 99);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new TranscriberModelProgress(
                                id, "downloading", percent,
                                $"Downloading {model.Name} — {percent}%"));
                        }
                    }
                }
            }

            if (total > 0 && copied != total)
            {
                throw new InvalidDataException("The download was incomplete. Try again.");
            }

            ModelIntegrity.Verify(ModelIntegrity.Finish(hash), expectedHash, model.Name);

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

    private static TranscriberModel Model(string id, string language, string name, string tier, int sizeMb, string file, string note, int vramMb = 0) =>
        new(id, language, name, tier, sizeMb,
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + file, note,
            Hashes.TryGetValue(file, out string? sha256) ? sha256 : null, vramMb);

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
