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
            : AppSettings.ConfiguredUserDirectory is { Length: > 0 } custom
                ? custom
                : DefaultUserRoot;

    public static string DefaultUserRoot => Path.Combine(DocumentsRoot(), "Ruse");

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
    public static string Notes => Path.Combine(UserRoot, "Notes");
    public static string Support => Path.Combine(UserRoot, "Support");

    /// <summary>Old top-level folder used before the Ruse folder was grouped.</summary>
    public static string LegacySamples => Path.Combine(MyMusicOrDocuments(), "Ruse Samples");

    /// <summary>Old top-level folder used before the Ruse folder was grouped.</summary>
    public static string LegacyRecordings => Path.Combine(MyMusicOrDocuments(), "Ruse Recordings");

    public static void EnsureAll()
    {
        foreach (string directory in new[] { UserRoot, Presets, Recordings, Samples, Exports, Notes, Support })
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
        OpenFolder(UserRoot);
    }

    public static void OpenFolder(string directory)
    {
        Ensure(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
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
