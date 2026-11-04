using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers;

namespace Ruse.App;

internal sealed record TtsModel(
    string Id,
    string Name,
    string Family,
    string Languages,
    double SizeMb,
    string Url,
    string Sha256,
    string ArchiveRoot,
    string[] RequiredFiles,
    string[] Voices,
    string License,
    string Note);

internal sealed record TtsModelProgress(string Id, string Phase, double Progress, string? Error = null);

/// <summary>
/// Downloadable, offline text-to-speech models. Each archive contains the
/// complete model plus its lexicon or phonemizer data, so synthesis never
/// needs a network connection after installation.
/// </summary>
internal static class TtsModels
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateHttpClient();

    public static IReadOnlyList<TtsModel> All { get; } = new[]
    {
        new TtsModel(
            "supertonic-3-int8",
            "Supertonic 3",
            "supertonic",
            "31 languages",
            123,
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/sherpa-onnx-supertonic-3-tts-int8-2026-05-11.tar.bz2",
            "82fa96f91c4ef8abaae3a14a3f4153facf88bed821d1f7331cec2700f432c427",
            "sherpa-onnx-supertonic-3-tts-int8-2026-05-11",
            new[]
            {
                "duration_predictor.int8.onnx",
                "text_encoder.int8.onnx",
                "vector_estimator.int8.onnx",
                "vocoder.int8.onnx",
                "tts.json",
                "unicode_indexer.bin",
                "voice.bin",
            },
            new[] { "F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5" },
            "OpenRAIL",
            "Fast multilingual voice model. English, Portuguese, Spanish, French, German, Italian, Japanese, Korean, Chinese, and more."),
        new TtsModel(
            "kokoro-int8-multi-lang-v1_1",
            "Kokoro 82M",
            "kokoro",
            "English + Chinese",
            140,
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2",
            "259c9c8bc906d6078e4e0570421885dabb68b539c2f970fcbb890a58eeaf3e9c",
            "kokoro-int8-multi-lang-v1_1",
            new[]
            {
                "model.int8.onnx",
                "voices.bin",
                "tokens.txt",
                "lexicon-us-en.txt",
                "lexicon-zh.txt",
            },
            CreateVoices("Voice", 103),
            "Apache-2.0",
            "High-quality English and Chinese voices with 103 speaker styles."),
    };

    private static string[] CreateVoices(string prefix, int count)
    {
        return Enumerable.Range(1, count).Select(index => $"{prefix} {index}").ToArray();
    }
    
    public static string DefaultId => All[0].Id;
    public static string Root => Path.Combine(AppSettings.Directory, "models", "tts");

    public static TtsModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string PathFor(string id) => Path.Combine(Root, id);

    public static bool IsInstalled(string? id)
    {
        var model = Find(id);
        if (model == null)
        {
            return false;
        }

        string dir = PathFor(model.Id);
        return model.RequiredFiles.All(file => File.Exists(Path.Combine(dir, file)));
    }

    public static string Resolve(string? id) =>
        IsInstalled(id) ? Find(id)!.Id : DefaultId;

    public static string GetInstalledPath(string id) => PathFor(Find(id)?.Id ?? id);

    public static long GetInstalledSizeBytes() => DirectoryBytes(Root);

    public static long GetInstalledSizeBytes(string id) => DirectoryBytes(PathFor(id));

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    public static int InstalledCount => All.Count(model => IsInstalled(model.Id));

    public static string FormatSize(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024:0.0} GB" : $"{megabytes:0} MB";

    public static async Task DownloadAsync(
        string id,
        IProgress<TtsModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        var model = Find(id) ?? throw new InvalidOperationException("Unknown text-to-speech model.");
        if (IsInstalled(model.Id))
        {
            progress?.Report(new TtsModelProgress(model.Id, "done", 1));
            return;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string archivePath = Path.Combine(Root, model.Id + ".tar.bz2.part");
        string extractPath = Path.Combine(Root, model.Id + ".extract-" + Guid.NewGuid().ToString("N"));
        string finalPath = PathFor(model.Id);
        try
        {
            Directory.CreateDirectory(Root);
            DeleteFile(archivePath);
            progress?.Report(new TtsModelProgress(model.Id, "downloading", 0));

            using var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            long received = 0;
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                double ratio = total > 0 ? (double)received / total : 0;
                progress?.Report(new TtsModelProgress(model.Id, "downloading", Math.Clamp(ratio * 0.85, 0, 0.85)));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Dispose();
            if (total > 0 && received != total)
            {
                throw new InvalidDataException("The text-to-speech model download was incomplete.");
            }

            ModelIntegrity.Verify(ModelIntegrity.Finish(hash), model.Sha256, model.Name);

            progress?.Report(new TtsModelProgress(model.Id, "installing", 0.86));
            Directory.CreateDirectory(extractPath);
            ExtractArchive(archivePath, extractPath);
            string source = Path.Combine(extractPath, model.ArchiveRoot);
            if (!Directory.Exists(source))
            {
                source = Directory.EnumerateDirectories(extractPath).FirstOrDefault()
                    ?? throw new InvalidDataException("The text-to-speech model archive is empty.");
            }

            if (Directory.Exists(finalPath))
            {
                Directory.Delete(finalPath, true);
            }

            Directory.Move(source, finalPath);
            if (!IsInstalled(model.Id))
            {
                throw new InvalidDataException("The text-to-speech model archive is missing required files.");
            }

            progress?.Report(new TtsModelProgress(model.Id, "done", 1));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new TtsModelProgress(model.Id, "cancelled", 0));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new TtsModelProgress(model.Id, "error", 0, ex.Message));
            throw;
        }
        finally
        {
            DeleteFile(archivePath);
            try
            {
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
            }
            catch
            {
            }

            Gate.Release();
        }
    }

    public static void Delete(string id)
    {
        string dir = PathFor(Find(id)?.Id ?? id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination);
        string tarPath = archivePath + ".tar";
        try
        {
            using (var file = File.OpenRead(archivePath))
            using (var bzip = BZip2Stream.Create(file, CompressionMode.Decompress, false, leaveOpen: true))
            using (var tar = File.Create(tarPath))
            {
                bzip.CopyTo(tar);
            }

            using var archive = ArchiveFactory.OpenArchive(tarPath, new ReaderOptions());
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                {
                    continue;
                }

                string key = entry.Key.Replace('\\', '/');
                if (key.StartsWith('/') || key.Split('/').Any(part => part == ".."))
                {
                    continue;
                }

                string path = Path.GetFullPath(Path.Combine(root, key));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(path);
                input.CopyTo(output);
            }
        }
        finally
        {
            DeleteFile(tarPath);
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ruse/0.5");
        return client;
    }
}
