using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record VoskModelProgress(string Id, string Phase, double Progress, string? Error = null);

/// <summary>English small Vosk model used for low-latency partial transcripts.</summary>
internal static class VoskModels
{
    public const string Id = "vosk-small-en-us-0.15";
    public const string Name = "Vosk Small English";
    private const double SizeMb = 40;
    private const string Url = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const string Sha256 = "30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498";
    private const string ArchiveRoot = "vosk-model-small-en-us-0.15";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new();

    public static string Root => Path.Combine(ModelStorage.Root, "vosk");
    public static string PathFor => Path.Combine(Root, ArchiveRoot);

    public static bool IsInstalled =>
        File.Exists(Path.Combine(PathFor, "am", "final.mdl")) &&
        File.Exists(Path.Combine(PathFor, "conf", "model.conf")) &&
        File.Exists(Path.Combine(PathFor, "graph", "HCLr.fst")) &&
        File.Exists(Path.Combine(PathFor, "graph", "Gr.fst"));

    public static long GetInstalledSizeBytes()
    {
        if (!Directory.Exists(PathFor)) return 0;
        try
        {
            return Directory.EnumerateFiles(PathFor, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    public static async Task DownloadAsync(IProgress<VoskModelProgress>? progress, CancellationToken token)
    {
        if (IsInstalled)
        {
            progress?.Report(new VoskModelProgress(Id, "done", 1));
            return;
        }

        ModelPreflight.Check(Root, SizeMb, diskMultiplier: 2.0);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        string archive = Path.Combine(Root, ArchiveRoot + ".zip.part");
        string extract = Path.Combine(Root, ".extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Root);
            progress?.Report(new VoskModelProgress(Id, "downloading", 0));
            using var response = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            long received = 0;
            using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                double ratio = total > 0 ? (double)received / total : 0;
                progress?.Report(new VoskModelProgress(Id, "downloading", Math.Clamp(ratio * 0.85, 0, 0.85)));
            }

            await output.FlushAsync(token).ConfigureAwait(false);
            output.Dispose();
            if (total > 0 && received != total)
            {
                throw new InvalidDataException("The Vosk model download was incomplete.");
            }

            ModelIntegrity.Verify(ModelIntegrity.Finish(hash), Sha256, Name);

            progress?.Report(new VoskModelProgress(Id, "installing", 0.86));
            Directory.CreateDirectory(extract);
            SafeZip.ExtractToDirectory(archive, extract, token);
            string source = Path.Combine(extract, ArchiveRoot);
            if (!Directory.Exists(source))
            {
                source = Directory.EnumerateDirectories(extract).FirstOrDefault()
                    ?? throw new InvalidDataException("The Vosk model archive is empty.");
            }

            if (Directory.Exists(PathFor)) Directory.Delete(PathFor, true);
            Directory.Move(source, PathFor);
            if (!IsInstalled)
            {
                throw new InvalidDataException("The Vosk model archive is missing required files.");
            }

            progress?.Report(new VoskModelProgress(Id, "done", 1));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new VoskModelProgress(Id, "cancelled", 0));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new VoskModelProgress(Id, "error", 0, ex.Message));
            AppLog.Error("Could not install the Vosk model.", ex);
            throw;
        }
        finally
        {
            TryDelete(archive);
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
            Gate.Release();
        }
    }

    public static void Delete()
    {
        VoskModelCache.Delete();
        DeleteFiles();
    }

    internal static void DeleteFiles()
    {
        try
        {
            if (Directory.Exists(PathFor)) Directory.Delete(PathFor, true);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch
        {
            // The cache retries after the last active recognizer releases it.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
