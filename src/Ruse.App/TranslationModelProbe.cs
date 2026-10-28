using System.Diagnostics;
using System.IO;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Checks a GGUF model in a child process so a native crash cannot kill the app.</summary>
internal static class TranslationModelProbe
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
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Cache) Cache.Remove(CacheKey(path));
    }

    public static void Clear()
    {
        lock (Cache) Cache.Clear();
    }

    public static void Run(string path, string resultPath)
    {
        try
        {
            File.WriteAllText(resultPath, "LOADING");
            var parameters = new ModelParams(path)
            {
                ContextSize = 256,
                BatchSize = 32,
                GpuLayerCount = 0,
                Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
            };
            using var weights = LLamaWeights.LoadFromFile(parameters);
            File.WriteAllText(resultPath, "LOADED");
            var executor = new StatelessExecutor(weights, parameters)
            {
                ApplyTemplate = true,
                SystemMessage = "Reply with one word.",
            };
            var inference = new InferenceParams
            {
                MaxTokens = 1,
                SamplingPipeline = new GreedySamplingPipeline(),
            };
            File.WriteAllText(resultPath, "INFERRING");
            Task.Run(async () =>
            {
                await foreach (var _ in executor.InferAsync("Hello", inference))
                {
                    break;
                }
            }).GetAwaiter().GetResult();

            long peak = Process.GetCurrentProcess().PeakWorkingSet64;
            File.WriteAllText(resultPath, $"OK {peak / (1024 * 1024)}");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(resultPath, $"ERROR {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }

            Environment.Exit(3);
        }
    }

    public static string TranslateText(string path, string targetCode, string text)
    {
        var parameters = new ModelParams(path)
        {
            ContextSize = 2048,
            BatchSize = 256,
            GpuLayerCount = 0,
            Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        };
        using var weights = LLamaWeights.LoadFromFile(parameters);
        var executor = new StatelessExecutor(weights, parameters)
        {
            ApplyTemplate = true,
            SystemMessage = TranslationPrompt.System(targetCode),
        };
        var inference = new InferenceParams
        {
            MaxTokens = Math.Clamp(text.Length * 2 + 64, 64, 512),
            SamplingPipeline = new GreedySamplingPipeline(),
        };
        var result = new System.Text.StringBuilder();
        Task.Run(async () =>
        {
            await foreach (var piece in executor.InferAsync(text, inference))
            {
                result.Append(piece);
            }
        }).GetAwaiter().GetResult();
        return result.ToString().Trim();
    }

    private static async Task<ModelProbeResult> ProbeAsync(string modelId, string path, CancellationToken token)
    {
        string resultPath = Path.Combine(
            Path.GetTempPath(),
            "ruse-translation-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Ruse.exe");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--translation-probe");
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
                if (token.IsCancellationRequested) throw;
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

                AppLog.Info($"Translation model '{modelId}' passed its load check ({peak} MB peak).");
                return new ModelProbeResult(true, "Model check passed.", peak);
            }

            if (text.StartsWith("ERROR", StringComparison.Ordinal))
            {
                string message = text.Length > 5 ? text[5..].Trim() : "Unknown error.";
                return new ModelProbeResult(false, "This translation model could not be loaded. " + message, 0);
            }

            string code = process.ExitCode < 0
                ? $"0x{(uint)process.ExitCode:X8}"
                : process.ExitCode.ToString();
            return new ModelProbeResult(false,
                $"The translation runtime crashed while loading this model (exit code {code}). " +
                "The model may be corrupted or incompatible with this build. Re-download it or choose another tier.",
                0);
        }
    }

    private static string CacheKey(string path)
    {
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
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


