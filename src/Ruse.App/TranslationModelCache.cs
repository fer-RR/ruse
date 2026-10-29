using LLama;
using LLama.Common;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Keeps one set of GGUF weights loaded per model, shared by every translator node.</summary>
internal static class TranslationModelCache
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (LLamaWeights Weights, int References, bool Gpu)> Models = new();
    private static readonly HashSet<string> PendingDrops = new();

    public static LLamaWeights? Acquire(string modelId, string path, CancellationToken token)
    {
        lock (Lock)
        {
            if (Models.TryGetValue(modelId, out var existing))
            {
                PendingDrops.Remove(modelId);
                Models[modelId] = (existing.Weights, existing.References + 1, existing.Gpu);
                return existing.Weights;
            }

            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                bool gpu = ModelRuntime.UseGpu;
                LLamaWeights weights;
                try
                {
                    weights = LLamaWeights.LoadFromFile(
                        ModelRuntime.TranslationParams(path, 256, null, threads));
                }
                catch (Exception gpuError) when (gpu)
                {
                    AppLog.Warning(
                        $"GPU load failed for translation model '{modelId}'; falling back to CPU. " +
                        gpuError.GetBaseException().Message);
                    ModelRuntime.ReportGpuFallback(
                        $"Translation model '{modelId}': {gpuError.GetBaseException().Message}");
                    gpu = false;
                    weights = LLamaWeights.LoadFromFile(new ModelParams(path)
                    {
                        GpuLayerCount = 0,
                        Threads = threads,
                        BatchSize = 256,
                    });
                }

                if (token.IsCancellationRequested)
                {
                    weights.Dispose();
                    return null;
                }

                Models[modelId] = (weights, 1, gpu);
                AppLog.Info($"Translation model '{modelId}' loaded.");
                return weights;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not load translation model '{modelId}'.", ex);
                return null;
            }
        }
    }

    public static bool IsGpu(string modelId)
    {
        lock (Lock)
        {
            return Models.TryGetValue(modelId, out var entry) && entry.Gpu;
        }
    }

    public static void Release(string modelId)
    {
        lock (Lock)
        {
            if (!Models.TryGetValue(modelId, out var entry)) return;
            if (entry.References > 1)
            {
                Models[modelId] = (entry.Weights, entry.References - 1, entry.Gpu);
                return;
            }

            Models.Remove(modelId);
            PendingDrops.Remove(modelId);
            entry.Weights.Dispose();
        }
    }

    public static void Drop(string modelId)
    {
        LLamaWeights? weights = null;
        lock (Lock)
        {
            if (!Models.TryGetValue(modelId, out var entry)) return;
            if (entry.References > 0)
            {
                PendingDrops.Add(modelId);
                return;
            }

            Models.Remove(modelId);
            PendingDrops.Remove(modelId);
            weights = entry.Weights;
        }

        weights?.Dispose();
    }

    public static void DropAll()
    {
        List<LLamaWeights> weights;
        lock (Lock)
        {
            weights = Models
                .Where(pair => pair.Value.References == 0)
                .Select(pair => pair.Value.Weights)
                .ToList();
            foreach (var pair in Models.Where(pair => pair.Value.References == 0).ToList())
            {
                Models.Remove(pair.Key);
            }

            PendingDrops.Clear();
        }

        foreach (var item in weights) item.Dispose();
    }
}
