using System.Diagnostics;
using System.IO;

namespace Ruse.App;

/// <summary>
/// User-facing content folders. Settings and logs stay in
/// <see cref="AppSettings.Directory"/>; downloaded models live under
/// LocalAppData; presets, recordings, samples and exports live together in
/// Documents\Ruse so they are easy to find and browse.
/// </summary>
internal static class RuseFolders
{
    public static string UserRoot =>
        AppSettings.OverrideDirectory != null
            ? Path.Combine(AppSettings.OverrideDirectory, "User")
            : Path.Combine(DocumentsRoot(), "Ruse");

    private static string DocumentsRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return string.IsNullOrWhiteSpace(root) ? AppSettings.Directory : root;
    }

    public static string Presets => Path.Combine(UserRoot, "Presets");
    public static string Recordings => Path.Combine(UserRoot, "Recordings");
    public static string Samples => Path.Combine(UserRoot, "Samples");
    public static string Exports => Path.Combine(UserRoot, "Exports");

    /// <summary>Old top-level folder used before the Ruse folder was grouped.</summary>
    public static string LegacySamples => Path.Combine(MyMusicOrDocuments(), "Ruse Samples");

    /// <summary>Old top-level folder used before the Ruse folder was grouped.</summary>
    public static string LegacyRecordings => Path.Combine(MyMusicOrDocuments(), "Ruse Recordings");

    public static void EnsureAll()
    {
        foreach (string directory in new[] { UserRoot, Presets, Recordings, Samples, Exports })
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                // A missing user folder should not stop Ruse from starting.
            }
        }
    }

    public static string Ensure(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
        }

        return directory;
    }

    public static void OpenUserFolder()
    {
        EnsureAll();
        Process.Start(new ProcessStartInfo
        {
            FileName = UserRoot,
            UseShellExecute = true,
        });
    }

    private static string MyMusicOrDocuments()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return string.IsNullOrWhiteSpace(root) ? AppSettings.Directory : root;
    }
}
