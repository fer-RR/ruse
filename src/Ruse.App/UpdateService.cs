using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ruse.App;

internal sealed class UpdateState
{
    public string Status { get; set; } = "idle";
    public string CurrentVersion { get; set; } = "";
    public string? LatestVersion { get; set; }
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? Error { get; set; }
    public double Progress { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public bool CanApply { get; set; }

    public UpdateState Clone() => (UpdateState)MemberwiseClone();
}

internal sealed class UpdateService : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/fer-RR/ruse/releases/latest";

    private sealed record ReleaseInfo(
        Version Version,
        string Name,
        string Notes,
        string ReleaseUrl,
        string ZipName,
        string ZipUrl,
        string ShaName,
        string ShaUrl,
        long ZipSize);

    private readonly WebShellController _owner;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly UpdateState _state;
    private readonly string _updatesRoot;
    private ReleaseInfo? _release;
    private string? _sourceDirectory;
    private int _busy;
    private long _lastProgressTick;

    public UpdateService(WebShellController owner)
    {
        _owner = owner;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Ruse/{CurrentVersionText}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ruse", "updates");
        _state = new UpdateState { CurrentVersion = CurrentVersionText };
        CleanupOldFiles();
    }

    public static string CurrentVersionText
    {
        get
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
    }

    private static Version CurrentVersion
    {
        get
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public void PublishState()
    {
        UpdateState snapshot;
        lock (_gate)
        {
            snapshot = _state.Clone();
        }

        _owner.PostUpdateState(snapshot);
    }

    public async Task MaybeAutoCheckAsync()
    {
        AppSettings settings = _owner.UserSettings;
        if (!settings.UpdateAutoCheck)
        {
            return;
        }

        int days = settings.UpdateCheckIntervalDays is 1 or 7 or 30
            ? settings.UpdateCheckIntervalDays
            : 1;
        DateTime? lastCheck = settings.LastUpdateCheckUtc;
        if (lastCheck.HasValue &&
            DateTime.UtcNow - lastCheck.Value.ToUniversalTime() < TimeSpan.FromDays(days))
        {
            return;
        }

        await CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_state.Status is "downloading" or "ready" or "applying")
                {
                    return;
                }
            }

            SetState(state =>
            {
                state.Status = "checking";
                state.Error = null;
                state.LatestVersion = null;
                state.Name = null;
                state.Notes = null;
                state.ReleaseUrl = null;
                state.Progress = 0;
                state.DownloadedBytes = 0;
                state.TotalBytes = 0;
                state.CanApply = false;
            });
            _owner.RecordUpdateCheck();

            using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            string json = await _http.GetStringAsync(ApiUrl, checkTimeout.Token);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";
            string name = root.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? tag
                : tag;
            string notes = root.TryGetProperty("body", out JsonElement bodyElement)
                ? bodyElement.GetString() ?? ""
                : "";
            string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
                ? urlElement.GetString() ?? ""
                : "";

            string versionText = tag.TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(versionText, out Version? latest) ||
                latest.Major < 0)
            {
                throw new InvalidDataException("The latest release does not have a valid version tag.");
            }

            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= CurrentVersion)
            {
                SetState(state => state.Status = "current");
                return;
            }

            string? zipName = null;
            string? zipUrl = null;
            string? shaName = null;
            string? shaUrl = null;
            long zipSize = 0;
            if (root.TryGetProperty("assets", out JsonElement assets))
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out JsonElement assetNameElement)
                        ? assetNameElement.GetString() ?? ""
                        : "";
                    if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        !assetName.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    zipName = assetName;
                    zipUrl = asset.TryGetProperty("browser_download_url", out JsonElement zipUrlElement)
                        ? zipUrlElement.GetString()
                        : null;
                    zipSize = asset.TryGetProperty("size", out JsonElement sizeElement)
                        ? sizeElement.GetInt64()
                        : 0;
                    break;
                }

                if (zipName != null)
                {
                    shaName = Path.GetFileNameWithoutExtension(zipName) + ".sha256";
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string assetName = asset.TryGetProperty("name", out JsonElement assetNameElement)
                            ? assetNameElement.GetString() ?? ""
                            : "";
                        if (!string.Equals(assetName, shaName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        shaUrl = asset.TryGetProperty("browser_download_url", out JsonElement shaUrlElement)
                            ? shaUrlElement.GetString()
                            : null;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(zipName) ||
                !IsTrustedReleaseUrl(zipUrl) ||
                string.IsNullOrWhiteSpace(shaName) ||
                !IsTrustedReleaseUrl(shaUrl))
            {
                throw new InvalidDataException("The latest release is missing its Windows x64 package or checksum.");
            }

            _release = new ReleaseInfo(
                latest,
                string.IsNullOrWhiteSpace(name) ? $"Ruse {latest}" : name,
                notes,
                releaseUrl,
                zipName,
                zipUrl!,
                shaName,
                shaUrl!,
                zipSize);

            SetState(state =>
            {
                state.Status = "available";
                state.LatestVersion = latest.ToString(3);
                state.Name = _release.Name;
                state.Notes = _release.Notes;
                state.ReleaseUrl = _release.ReleaseUrl;
                state.TotalBytes = zipSize;
                state.Progress = 0;
            });
        }
        catch (Exception ex)
        {
            Fail($"Could not check for updates. {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public async Task DownloadAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            return;
        }

        try
        {
            ReleaseInfo? release;
            lock (_gate)
            {
                release = _release;
                if (release == null || _state.Status != "available")
                {
                    return;
                }
            }

            SetState(state =>
            {
                state.Status = "downloading";
                state.Error = null;
                state.Progress = 0;
                state.DownloadedBytes = 0;
                state.TotalBytes = release.ZipSize;
                state.CanApply = false;
            });

            string versionFolder = SanitizeFolderName(release.Version.ToString(3));
            string directory = Path.Combine(_updatesRoot, versionFolder);
            Directory.CreateDirectory(directory);

            string zipPath = Path.Combine(directory, release.ZipName);
            string shaPath = Path.Combine(directory, release.ShaName);
            string partialPath = zipPath + ".part";
            File.Delete(partialPath);

            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await DownloadFileAsync(
                release.ZipUrl, partialPath, release.ZipSize, reportProgress: true, downloadTimeout.Token);
            File.Move(partialPath, zipPath, overwrite: true);

            await DownloadFileAsync(
                release.ShaUrl, shaPath, 0, reportProgress: false, downloadTimeout.Token);
            string expectedHash = (await File.ReadAllTextAsync(shaPath))
                .Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            string actualHash = await ComputeSha256Async(zipPath);
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zipPath);
                throw new InvalidDataException("The downloaded package did not match the published SHA-256.");
            }

            string stageDirectory = Path.Combine(directory, "stage");
            if (Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipPath, stageDirectory, overwriteFiles: true);
            string sourceDirectory = Path.Combine(stageDirectory, "Ruse");
            if (!File.Exists(Path.Combine(sourceDirectory, "Ruse.exe")))
            {
                if (File.Exists(Path.Combine(stageDirectory, "Ruse.exe")))
                {
                    sourceDirectory = stageDirectory;
                }
                else
                {
                    throw new InvalidDataException("The update package does not contain Ruse.exe.");
                }
            }

            _sourceDirectory = sourceDirectory;
            SetState(state =>
            {
                state.Status = "ready";
                state.Progress = 1;
                state.DownloadedBytes = state.TotalBytes;
                state.CanApply = true;
            });
        }
        catch (Exception ex)
        {
            Fail($"The update could not be downloaded. {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Apply()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            return;
        }

        try
        {
            string? sourceDirectory = _sourceDirectory;
            lock (_gate)
            {
                if (_state.Status != "ready" || sourceDirectory == null)
                {
                    return;
                }
            }

            string executable = Path.Combine(sourceDirectory, "Ruse.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("The staged update is missing Ruse.exe.", executable);
            }

            string installDirectory = AppContext.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsWritableDirectory(installDirectory))
            {
                throw new UnauthorizedAccessException(
                    "The Ruse folder is not writable. Download the update from GitHub and replace the folder manually.");
            }

            string logPath = Path.Combine(_updatesRoot, "update.log");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = sourceDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(sourceDirectory);
            startInfo.ArgumentList.Add("--restart");
            startInfo.ArgumentList.Add(Path.Combine(installDirectory, "Ruse.exe"));
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(logPath);

            if (Process.Start(startInfo) == null)
            {
                throw new InvalidOperationException("The update helper could not be started.");
            }

            SetState(state =>
            {
                state.Status = "applying";
                state.CanApply = false;
            });
            _owner.ShutdownForUpdate();
        }
        catch (Exception ex)
        {
            Fail($"The update could not be started. {ex.Message}");
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private void SetState(Action<UpdateState> mutate)
    {
        UpdateState snapshot;
        lock (_gate)
        {
            mutate(_state);
            snapshot = _state.Clone();
        }

        _owner.PostUpdateState(snapshot);
    }

    private void Fail(string message)
    {
        SetState(state =>
        {
            state.Status = "error";
            state.Error = message;
            state.CanApply = false;
            state.Progress = 0;
        });
    }

    private async Task DownloadFileAsync(
        string url, string path, long expectedSize, bool reportProgress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? expectedSize;

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        byte[] buffer = new byte[64 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (!reportProgress)
            {
                continue;
            }

            long now = Environment.TickCount64;
            if (now - _lastProgressTick < 200 && downloaded != total)
            {
                continue;
            }

            _lastProgressTick = now;
            long snapshotBytes = downloaded;
            long snapshotTotal = total;
            SetState(state =>
            {
                state.Status = "downloading";
                state.DownloadedBytes = snapshotBytes;
                state.TotalBytes = snapshotTotal;
                state.Progress = snapshotTotal > 0
                    ? Math.Clamp((double)snapshotBytes / snapshotTotal, 0, 1)
                    : 0;
            });
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsTrustedReleaseUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith(
            "/fer-RR/ruse/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".ruse-update-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFolderName(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(_updatesRoot))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (string directory in Directory.GetDirectories(_updatesRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                }
            }

            string installDirectory = AppContext.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(installDirectory);
            string name = Path.GetFileName(installDirectory);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            DateTime backupCutoff = DateTime.UtcNow.AddDays(-30);
            foreach (string directory in Directory.GetDirectories(parent, name + ".backup-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < backupCutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
