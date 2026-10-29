using LLama.Common;
using LLama.Native;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Chooses the CPU or Vulkan backend shared by speech and translation.</summary>
internal static class ModelRuntime
{
    /// <summary>When true, model loading asks for GPU offload; CPU remains the fallback.</summary>
    public static bool UseGpu { get; set; }

    /// <summary>True when the Vulkan probe found a physical device.</summary>
    public static bool GpuAvailable { get; private set; }

    /// <summary>Name of the first Vulkan device, or null when none was found.</summary>
    public static string? GpuDeviceName { get; private set; }

    /// <summary>Why the Vulkan device could not be used, if applicable.</summary>
    public static string? GpuStatusError { get; private set; }

    /// <summary>Why the last GPU model load fell back to the CPU, if it did.</summary>
    public static string? GpuFallbackReason { get; private set; }

    /// <summary>
    /// Loads the Vulkan build when it is present. WithAutoFallback keeps the app
    /// working on machines without a Vulkan driver.
    /// </summary>
    public static void ConfigureNativeLibraries()
    {
        try
        {
            NativeLibraryConfig.All.WithVulkan(true).WithAutoFallback(true);
        }
        catch (Exception ex)
        {
            GpuStatusError = "Vulkan configuration failed: " + ex.GetBaseException().Message;
            AppLog.Warning("Could not configure the Vulkan backend; using CPU. " + ex.Message);
        }

        var probe = VulkanProbe.Query();
        GpuAvailable = probe.Available;
        GpuDeviceName = probe.DeviceName;
        if (probe.Error != null)
        {
            GpuStatusError = probe.Error;
        }
    }

    public static void ReportGpuFallback(string reason)
    {
        GpuFallbackReason = reason.Length > 240 ? reason[..240] : reason;
    }

    public static void ClearGpuFallback()
    {
        GpuFallbackReason = null;
    }

    public static ModelParams TranslationParams(string path, uint batchSize, uint? contextSize, int threads) =>
        new(path)
        {
            ContextSize = contextSize,
            BatchSize = batchSize,
            GpuLayerCount = UseGpu ? 99 : 0,
            Threads = threads,
        };
}
