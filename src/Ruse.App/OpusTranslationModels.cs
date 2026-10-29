using System.IO;
using System.Net.Http;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record OpusTranslationModel(
    string Id,
    string Name,
    string Source,
    string Target,
    string Repo,
    int SizeMb,
    string License,
    string? Prefix = null);

internal sealed record OpusTranslationPlan(
    IReadOnlyList<string> ModelIds,
    string? Error,
    string Source,
    string Target)
{
    public bool IsEmpty => ModelIds.Count == 0 && Error == null;
}

/// <summary>Optional OPUS-MT models converted to ONNX Runtime by Xenova.</summary>
internal static class OpusTranslationModels
{
    private const long MinimumModelSize = 10L * 1024 * 1024;
    private const long MinimumTextSize = 100 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateHttpClient();

    public static IReadOnlyList<OpusTranslationModel> All { get; } = new[]
    {
        Model("en-de", "English to German", "en", "de", "Xenova/opus-mt-en-de"),
        Model("en-es", "English to Spanish", "en", "es", "Xenova/opus-mt-en-es"),
        Model("en-fr", "English to French", "en", "fr", "Xenova/opus-mt-en-fr"),
        Model("en-it", "English to Italian", "en", "it", "Xenova/opus-mt-en-it"),
        Model("en-pt", "English to Portuguese", "en", "pt", "Xenova/opus-mt-en-ROMANCE",
            prefix: ">>pt<< "),
        Model("en-zh", "English to Chinese", "en", "zh", "Xenova/opus-mt-en-zh"),
        Model("de-en", "German to English", "de", "en", "Xenova/opus-mt-de-en"),
        Model("es-en", "Spanish to English", "es", "en", "Xenova/opus-mt-es-en"),
        Model("fr-en", "French to English", "fr", "en", "Xenova/opus-mt-fr-en"),
        Model("it-en", "Italian to English", "it", "en", "Xenova/opus-mt-it-en"),
        Model("pt-en", "Portuguese to English", "pt", "en", "Xenova/opus-mt-ROMANCE-en"),
        Model("ja-en", "Japanese to English", "ja", "en", "Xenova/opus-mt-ja-en"),
        Model("zh-en", "Chinese to English", "zh", "en", "Xenova/opus-mt-zh-en"),
        Model("ko-en", "Korean to English", "ko", "en", "Xenova/opus-mt-ko-en"),
    };

    public static string Root => Path.Combine(TranslationModels.Root, "opus");

    public static OpusTranslationModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string PathFor(string id) => Path.Combine(Root, id);

    public static bool IsInstalled(string? id)
    {
        var model = Find(id);
        if (model == null) return false;
        string directory = PathFor(model.Id);
        return RequiredFiles(model).All(file => IsValidFile(Path.Combine(directory, file.Local)));
    }

    public static long GetInstalledSizeBytes(string? id)
    {
        var model = Find(id);
        if (model == null || !IsInstalled(id)) return 0;
        try
        {
            return new DirectoryInfo(PathFor(model.Id))
                .EnumerateFiles()
                .Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    public static OpusTranslationPlan Resolve(string? sourceCode, string? targetCode)
    {
        string source = OpusCode(sourceCode);
        string target = OpusCode(targetCode);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return new OpusTranslationPlan(Array.Empty<string>(), null, source, target);
        }

        var direct = All.FirstOrDefault(model => model.Source == source && model.Target == target);
        if (direct != null)
        {
            return new OpusTranslationPlan(new[] { direct.Id }, null, source, target);
        }

        if (source != "en" && target != "en")
        {
            var first = All.FirstOrDefault(model => model.Source == source && model.Target == "en");
            var second = All.FirstOrDefault(model => model.Source == "en" && model.Target == target);
            if (first != null && second != null)
            {
                return new OpusTranslationPlan(new[] { first.Id, second.Id }, null, source, target);
            }
        }

        string pair = $"{source} to {target}";
        string message = source == "en" && target is "ja" or "ko"
            ? $"OPUS-MT does not include English to {target}. Use Qwen for this target."
            : $"OPUS-MT does not have a {pair} model. Install the English side or use Qwen.";
        return new OpusTranslationPlan(Array.Empty<string>(), message, source, target);
    }

    public static async Task DownloadAsync(
        string id,
        IProgress<TranslationModelProgress>? progress,
        CancellationToken token)
    {
        var model = Find(id) ?? throw new InvalidOperationException("Unknown OPUS-MT model.");
        if (IsInstalled(id)) return;

        await Gate.WaitAsync(token);
        string final = PathFor(id);
        string temp = final + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            if (IsInstalled(id)) return;
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(temp);

            var files = RequiredFiles(model).ToArray();
            int completed = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string target = Path.Combine(temp, file.Local);
                await DownloadFileAsync(model, file.Remote, target, progress, model, completed, files.Length, token);
                if (!IsValidFile(target))
                {
                    throw new InvalidDataException($"The downloaded {file.Local} is not valid.");
                }

                completed++;
            }

            if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
            Directory.Move(temp, final);
            progress?.Report(new TranslationModelProgress(
                "opus:" + id, "done", 100, $"{model.Name} ready"));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new TranslationModelProgress(
                "opus:" + id, "cancelled", 0, $"{model.Name} download cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new TranslationModelProgress(
                "opus:" + id, "error", 0, $"{model.Name} failed: {ex.Message}"));
            AppLog.Error($"Could not install OPUS-MT model '{id}'.", ex);
            throw;
        }
        finally
        {
            TryDeleteDirectory(temp);
            Gate.Release();
        }
    }

    public static void Delete(string id)
    {
        var model = Find(id);
        if (model == null) return;
        TryDeleteDirectory(PathFor(model.Id));
    }

    private static string OpusCode(string? code) => code?.ToLowerInvariant() switch
    {
        "pt" or "pt-br" or "pt_br" => "pt",
        "zh" or "zh-cn" or "zh_cn" or "zh-hans" => "zh",
        "ja" or "jap" => "ja",
        "ko" or "kor" => "ko",
        "en" or "eng" => "en",
        "de" or "deu" or "ger" => "de",
        "es" or "spa" => "es",
        "fr" or "fra" or "fre" => "fr",
        "it" or "ita" => "it",
        _ => TranslationLanguages.Find(code)?.WhisperCode ?? "en",
    };

    private static async Task DownloadFileAsync(
        OpusTranslationModel model,
        string remotePath,
        string target,
        IProgress<TranslationModelProgress>? progress,
        OpusTranslationModel display,
        int completed,
        int totalFiles,
        CancellationToken token)
    {
        string url = $"https://huggingface.co/{model.Repo}/resolve/main/{remotePath}";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        long length = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(target);
        var buffer = new byte[81920];
        long copied = 0;
        int lastPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read;
            int filePercent = length > 0 ? (int)Math.Clamp(copied * 100 / length, 0, 100) : 0;
            int percent = (int)Math.Clamp(
                ((completed + filePercent / 100.0) / totalFiles) * 100, 0, 99);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress?.Report(new TranslationModelProgress(
                    "opus:" + display.Id, "downloading", percent,
                    $"Downloading {display.Name} - {percent}%"));
            }
        }

        if (length > 0 && copied != length)
        {
            throw new InvalidDataException("The download was incomplete. Try again.");
        }
    }

    private static IEnumerable<(string Remote, string Local)> RequiredFiles(OpusTranslationModel model)
    {
        yield return ("onnx/encoder_model_quantized.onnx", "encoder.onnx");
        yield return ("onnx/decoder_model_quantized.onnx", "decoder.onnx");
        yield return ("vocab.json", "vocab.json");
        yield return ("source.spm", "source.spm");
        yield return ("config.json", "config.json");
    }

    private static bool IsValidFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (info.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                return info.Length >= MinimumModelSize;
            }

            return info.Length >= (info.Name == "config.json" ? 32 : MinimumTextSize);
        }
        catch
        {
            return false;
        }
    }

    private static OpusTranslationModel Model(
        string id,
        string name,
        string source,
        string target,
        string repo,
        string? prefix = null) =>
        new(id, name, source, target, repo, 110, "CC-BY-4.0", prefix);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ruse/1.0");
        return client;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
