using System.IO;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>Chooses between the in-process and isolated VST3 hosts.</summary>
public static class Vst3ProcessorFactory
{
    /// <summary>When true, VST3 nodes run in a child process.</summary>
    public static bool UseIsolation { get; set; } = true;

    private static readonly string[] PluginRoots = BuildPluginRoots();

    public static IVst3Processor Create(
        string identifier,
        string? stateBase64 = null,
        IReadOnlyDictionary<string, float>? pendingParameters = null,
        bool start = true)
    {
        if (!IsTrustedIdentifier(identifier))
        {
            throw new ArgumentException(
                "The VST3 plugin is not in a standard VST3 folder.", nameof(identifier));
        }

        if (UseIsolation)
        {
            try
            {
                return new IsolatedVst3Processor(identifier, stateBase64, pendingParameters, start);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Could not start the isolated VST3 host; using the in-process host. " + ex.Message);
            }
        }

        return new Vst3Processor(identifier, stateBase64, pendingParameters, start);
    }

    /// <summary>
    /// Accepts only VST3 bundles installed under the standard folders. A preset
    /// stores the bundle path, so loading arbitrary paths would let an untrusted
    /// preset run native code from anywhere (including a UNC share).
    /// </summary>
    public static bool IsTrustedIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            identifier.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(identifier))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(identifier);
        }
        catch
        {
            return false;
        }

        if (!full.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string root in PluginRoots)
        {
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] BuildPluginRoots()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(path);
                if (Path.IsPathFullyQualified(full))
                {
                    roots.Add(full);
                }
            }
            catch
            {
                // A missing special folder just removes one candidate root.
            }
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "VST3"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"));
        return roots.ToArray();
    }
}
