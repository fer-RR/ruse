using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
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

    // SHA-256 of the exact files published by the Xenova repositories.
    private static readonly Dictionary<string, string> PinnedHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Xenova/opus-mt-en-de|onnx/encoder_model_quantized.onnx"] = "15834b45fabd2dfb8c6c029b3ca3e7289aeefd90ece798ce42bcf548d1bd3b8d",
        ["Xenova/opus-mt-en-de|onnx/decoder_model_quantized.onnx"] = "75ef79aa9bde9e3dce9ca584c29507be5f464973f6c600c89ff419bc8de29ebc",
        ["Xenova/opus-mt-en-es|onnx/encoder_model_quantized.onnx"] = "03434edd1147e6c49e22066e9dc5888235e99cc35a6711d1c4ea5b06455d5da9",
        ["Xenova/opus-mt-en-es|onnx/decoder_model_quantized.onnx"] = "2b45074cc819f1d901e40510d7bc0781d99327d16c7eca04518c4ee0c3d6ba10",
        ["Xenova/opus-mt-en-fr|onnx/encoder_model_quantized.onnx"] = "0a81bdba62f53223740a8c6c6f58e716eba8ea1f92dfa569caf27ed5e3b6a0f7",
        ["Xenova/opus-mt-en-fr|onnx/decoder_model_quantized.onnx"] = "bdacc5f38e4d2b805a2fc9f38cf43844cdea56666e735137dbacf2a54a785257",
        ["Xenova/opus-mt-en-it|onnx/encoder_model_quantized.onnx"] = "b51c5110c7ba356ef01ee400d427afe162e657de1d7a2e9a40deedd811b49e6f",
        ["Xenova/opus-mt-en-it|onnx/decoder_model_quantized.onnx"] = "d5c77fd7f60eebdf81d3f6f6fecd8e6804529cbcbecc2bb742fd60f225e2cf3e",
        ["Xenova/opus-mt-en-ROMANCE|onnx/encoder_model_quantized.onnx"] = "b4ddfd9338ba3d7c6b9c2d1410ed94fb7e06741226918cef6148031101bbd019",
        ["Xenova/opus-mt-en-ROMANCE|onnx/decoder_model_quantized.onnx"] = "ec98bfea479f392987bc0eb5696dbf9b042556144c9da249688b54b462a068bc",
        ["Xenova/opus-mt-en-zh|onnx/encoder_model_quantized.onnx"] = "d3b7912bf6a9bd27e4c074c2df91d4ff3d5b4bc5f7f6c8d7cc9c805c98fbafee",
        ["Xenova/opus-mt-en-zh|onnx/decoder_model_quantized.onnx"] = "2c66a3981099b40edbb3a0d65e015d05964d42fecb9780f8422776eff5939112",
        ["Xenova/opus-mt-de-en|onnx/encoder_model_quantized.onnx"] = "4cedda8f8c89b72a42b3c6cd1e7a27f2de24457093e3bf80cb3e46829641fcd8",
        ["Xenova/opus-mt-de-en|onnx/decoder_model_quantized.onnx"] = "e44c1c4b50e8f51e49d4d5e54a9af1550dc74763d4023644a38af62611e6efc9",
        ["Xenova/opus-mt-es-en|onnx/encoder_model_quantized.onnx"] = "c01e70f8455efc350831aa2af7ed187b16829241f0018ba07d1ba643a391bc18",
        ["Xenova/opus-mt-es-en|onnx/decoder_model_quantized.onnx"] = "d518f1f0041b2831f0aeb5a087cf899742cd9d4b574f6b98ad9f816b8c06925b",
        ["Xenova/opus-mt-fr-en|onnx/encoder_model_quantized.onnx"] = "e727cb26ac6bf816394c49671af69c9ae5798868fed1e40849872415dffc1772",
        ["Xenova/opus-mt-fr-en|onnx/decoder_model_quantized.onnx"] = "8c5a0c5d113a1c4c5ae46e014aacf770f3bff2109fe8b74ead6aae5c5f408ffc",
        ["Xenova/opus-mt-it-en|onnx/encoder_model_quantized.onnx"] = "a025d2d85c90da23880116b67175e43e71921d8d2bf446b9f1eacdc86015aa4c",
        ["Xenova/opus-mt-it-en|onnx/decoder_model_quantized.onnx"] = "203bac7b98e438e65e1ddfcbec254ffb6811e6a8f4f71e93517ca9550323bf6e",
        ["Xenova/opus-mt-ROMANCE-en|onnx/encoder_model_quantized.onnx"] = "13750ba31ee80c88f6bbb28aa03320674c50394efb95f791d5c4f5c8894232bf",
        ["Xenova/opus-mt-ROMANCE-en|onnx/decoder_model_quantized.onnx"] = "955cc7be5a942ddef39e9721b7abd888837f41ba65983a86eb428b1bbf23e05c",
        ["Xenova/opus-mt-ja-en|onnx/encoder_model_quantized.onnx"] = "345262b16bcdda1468b0f3380c112b7ce79f731176b4b1d21f6edd5b2ae0d25c",
        ["Xenova/opus-mt-ja-en|onnx/decoder_model_quantized.onnx"] = "ec639efdbb8dd9a5c458ab3aeeaee34402b21200eb9c24a95a32a16f0bb22d66",
        ["Xenova/opus-mt-zh-en|onnx/encoder_model_quantized.onnx"] = "84d5e171b626bc8b6b220d022ac58696e9528c25deeacca62b5cbf4364547a99",
        ["Xenova/opus-mt-zh-en|onnx/decoder_model_quantized.onnx"] = "debcc3054b9ff9aaed972bd6e251a459abe1e2f1402da1efa63163dc2f46ef73",
        ["Xenova/opus-mt-ko-en|onnx/encoder_model_quantized.onnx"] = "210e1d2cb5b457cdeeffe288099cc9d1df9f13c35dc3d39fde4ba252af877380",
        ["Xenova/opus-mt-ko-en|onnx/decoder_model_quantized.onnx"] = "0c961ea33a62ea84e73c9ec94c0737e65d06b5f73a7d0398b018fbbb7262d929",
        ["Xenova/opus-mt-en-de|vocab.json"] = "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44",
        ["Xenova/opus-mt-en-de|source.spm"] = "678f2a1177d8389f67b66299762dcc4fc567e89b07e212ba91b0c56daecf47ce",
        ["Xenova/opus-mt-en-de|config.json"] = "517955ebf66cd6523f9982851dd41771b74fb2d9c00b4933ba2922ac7a63336f",
        ["Xenova/opus-mt-en-es|vocab.json"] = "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163",
        ["Xenova/opus-mt-en-es|source.spm"] = "4dd547c24816a335e7b0b2e63376a8f1b3cbfc671eda5ab808dd44fdadaa8791",
        ["Xenova/opus-mt-en-es|config.json"] = "499eae1f97eeba63172fc884f92fd727ae803b7ceb79c0f653aa4b011152af2f",
        ["Xenova/opus-mt-en-fr|vocab.json"] = "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d",
        ["Xenova/opus-mt-en-fr|source.spm"] = "173e9f493a668fe396d599e28d414a201193094e6ffd7a4678e5aab0f6d3d838",
        ["Xenova/opus-mt-en-fr|config.json"] = "b522b73fcc86f77981349c1df2a2e041eed0dbcbea4acf2635019cf21d51ffc0",
        ["Xenova/opus-mt-en-it|vocab.json"] = "40c98ffc0013f2f09ed9127418131738ad9c2de4d8f7f52d52532332bb2bf78c",
        ["Xenova/opus-mt-en-it|source.spm"] = "2efff2c37e51842c2817a6770280979b5dd803cebbe2928e576d42a888f89537",
        ["Xenova/opus-mt-en-it|config.json"] = "1cf40ab3aa16e8b6ea7c9766981de1da705a161564c0d5ad187c1e62e5c8ee85",
        ["Xenova/opus-mt-en-ROMANCE|vocab.json"] = "d9c3b6b01d3a9c9403befa60fd17e4a50dc4929698e3d6b9c2e7f066a9d1f041",
        ["Xenova/opus-mt-en-ROMANCE|source.spm"] = "b1f89e7b05828846baf19109a61029a36838f1c4f3defc218e6aee0ee2787864",
        ["Xenova/opus-mt-en-ROMANCE|config.json"] = "dc4f6f9ffbbf1196ff9e8329e5666fe190aef1b1c6806649d86b962b9bde292c",
        ["Xenova/opus-mt-en-zh|vocab.json"] = "22c957348eed495ee925afc40a36da3e387c8a34a734c8486967c2dca271613e",
        ["Xenova/opus-mt-en-zh|source.spm"] = "5775ddc9e3ff2fae91554da56468ad35ff56edaba870fea74447bc7234bfdaa8",
        ["Xenova/opus-mt-en-zh|config.json"] = "4727d1229a04f95bf6f39abf949d8080615433d99d6ebd85f81c09edd247d5fa",
        ["Xenova/opus-mt-de-en|vocab.json"] = "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44",
        ["Xenova/opus-mt-de-en|source.spm"] = "bbd1f495eea99c8e21ae086d9146e0fa7b096c3dfdd9ba07ab8b631889df5c9b",
        ["Xenova/opus-mt-de-en|config.json"] = "6754743ec4754957d8552dad869d1314f3af6626b67446ec4cebe5e2fbaa0fc4",
        ["Xenova/opus-mt-es-en|vocab.json"] = "b074b4cca0036ade5a39ea97faabd534e1015482c480fc2cb02c6481983eb163",
        ["Xenova/opus-mt-es-en|source.spm"] = "e236ee6d866b635c0142114f8647f39831f9d92534aa2aad75c942f6a78ad0e3",
        ["Xenova/opus-mt-es-en|config.json"] = "fab3a7f93185bc5aa7b419f6a1e6e74d98c8f2a506c94493d3019bf46da3478d",
        ["Xenova/opus-mt-fr-en|vocab.json"] = "f2ba9c69ae20f96b8bd821239a9152be422394f980350b77907cffc183db5f2d",
        ["Xenova/opus-mt-fr-en|source.spm"] = "78d0e717c77053f1c4b856d8661d9cb87c64f083a35418c087b9146300e4f585",
        ["Xenova/opus-mt-fr-en|config.json"] = "6c2851f154d7b88c7767e4fdcf8a5694a36a456bfa6cbcde1b13fdbade3b56f6",
        ["Xenova/opus-mt-it-en|vocab.json"] = "08e5b03ab20b2b9b85d41aed1b36dce2650afcb6e96274a06b7869b2e06884d5",
        ["Xenova/opus-mt-it-en|source.spm"] = "1bd307e2756991d470e4b40172147f7889b46a323b1a9a2150e66274825b1f21",
        ["Xenova/opus-mt-it-en|config.json"] = "4b82a23b7a91b213a934db70d0a33712a739fdd7c5686b02d89642d7dcfc7ac4",
        ["Xenova/opus-mt-ROMANCE-en|vocab.json"] = "32c8eeec099b448425a7da4e09ec10acbe424a733780c9bcec2062846f03e706",
        ["Xenova/opus-mt-ROMANCE-en|source.spm"] = "baeab21ec00d0b490382b82499ef0348235c5a8e75de28aec8290adf62b007c4",
        ["Xenova/opus-mt-ROMANCE-en|config.json"] = "df1876a346b1852ef4590bc74c6e180eb2ba10193cf2bded24c31876f357d83c",
        ["Xenova/opus-mt-ja-en|vocab.json"] = "a1667b43d021e09c76f385101bd473f20b5691a12cdca7dda43e4a8daff20021",
        ["Xenova/opus-mt-ja-en|source.spm"] = "d0b5c3b10b5959f056ff2c86e2f2356129242ff1fc72d3a4a34d6a8c0eee4e57",
        ["Xenova/opus-mt-ja-en|config.json"] = "d35476f1d3858883feec67e5a63ddc387452a70fda9c8e26db99c71aa2ad6a34",
        ["Xenova/opus-mt-zh-en|vocab.json"] = "08a119a1defd522fa047cb5e3bfe3e89633e96caa38ced0dc9cee7ef1021a011",
        ["Xenova/opus-mt-zh-en|source.spm"] = "e27a3a1b539f4959ec72ea60e453f49156289f95d4e6000b29332efc45616203",
        ["Xenova/opus-mt-zh-en|config.json"] = "293d318fce41dbf04114eac45037bb88a32d7c4ee21011a75e24a8b98ca45ad1",
        ["Xenova/opus-mt-ko-en|vocab.json"] = "97197044d4803c664a37f8ca2ced9c3405b8a3df6b262a12da22d990b0d756cd",
        ["Xenova/opus-mt-ko-en|source.spm"] = "c9496f7c2be9aecb84c751ae9f35a875915dde8e3892f652a5c76811ab2a0f49",
        ["Xenova/opus-mt-ko-en|config.json"] = "8260080db48141328ae198ede2b25483fa4a6f46dc2859784d6e21c188865e43",
    };


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
        ModelPreflight.Check(Root, model.SizeMb);

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

            token.ThrowIfCancellationRequested();
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

    public static async Task DeleteAsync(string id)
    {
        var model = Find(id);
        if (model == null) return;
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The OPUS-MT model is busy downloading. Cancel the download and try again.");
        }

        try
        {
            TryDeleteDirectory(PathFor(model.Id));
        }
        finally
        {
            Gate.Release();
        }
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
        string key = model.Repo + "|" + remotePath;
        string? expectedHash = PinnedHashes.TryGetValue(key, out string? pinned)
            ? pinned
            : ModelIntegrity.FromETag(response.Headers.ETag);
        if (expectedHash == null)
        {
            throw new InvalidDataException("No pinned SHA-256 is available for this OPUS-MT file.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(target);
        var buffer = new byte[81920];
        long copied = 0;
        int lastPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            hash.AppendData(buffer, 0, read);
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

        ModelIntegrity.Verify(ModelIntegrity.Finish(hash), expectedHash, display.Name);
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
