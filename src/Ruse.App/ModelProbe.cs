using System.Diagnostics;
using System.IO;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed record ModelProbeResult(bool Success, string Message, int PeakWorkingSetMb);

/// <summary>Checks a Whisper model in a child process so a native crash cannot kill the app.</summary>
internal static class ModelProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, ModelProbeResult> Cache = new();

    public static async Task<ModelProbeResult> EnsureAsync(string modelId, string path, CancellationToken token)
    {
        string key = CacheKey(path);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        await Gate.WaitAsync(token);
        try
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var result = await ProbeAsync(modelId, path, token);
            lock (Cache) Cache[key] = result;
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (Cache) Cache.Remove(CacheKey(path));
    }

    public static void Clear()
    {
        lock (Cache) Cache.Clear();
    }

    private static string CacheKey(string path)
    {
        // A re-download changes the timestamp, so stale results cannot persist.
        try
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path;
        }
    }

    private static async Task<ModelProbeResult> ProbeAsync(string modelId, string path, CancellationToken token)
    {
        string resultPath = Path.Combine(
            Path.GetTempPath(),
            "ruse-model-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Ruse.exe");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--model-probe");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(resultPath);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex)
        {
            return new ModelProbeResult(false, "Could not start the model check: " + ex.Message, 0);
        }

        if (process == null)
        {
            return new ModelProbeResult(false, "Could not start the model check.", 0);
        }

        using (process)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                TryDelete(resultPath);
                if (token.IsCancellationRequested)
                {
                    throw;
                }

                return new ModelProbeResult(false,
                    $"The model check timed out after {Timeout.TotalMinutes:0} minutes.", 0);
            }

            string text = "";
            try
            {
                if (File.Exists(resultPath))
                {
                    text = File.ReadAllText(resultPath).Trim();
                }
            }
            catch
            {
            }
            finally
            {
                TryDelete(resultPath);
            }

            if (process.ExitCode == 0 && text.StartsWith("OK", StringComparison.Ordinal))
            {
                int peak = 0;
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    int.TryParse(parts[1], out peak);
                }

                AppLog.Info($"Speech model '{modelId}' passed its load check ({peak} MB peak).");
                return new ModelProbeResult(true, "Model check passed.", peak);
            }

            if (text.StartsWith("ERROR", StringComparison.Ordinal))
            {
                string message = text.Length > 5 ? text[5..].Trim() : "Unknown error.";
                return new ModelProbeResult(false, "This model could not be loaded. " + message, 0);
            }

            string code = process.ExitCode < 0
                ? $"0x{(uint)process.ExitCode:X8}"
                : process.ExitCode.ToString();
            return new ModelProbeResult(false,
                $"The Whisper runtime crashed while loading this model (exit code {code}). " +
                "The model may be corrupted or incompatible with this build. " +
                "Re-download it or choose another tier.", 0);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
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
