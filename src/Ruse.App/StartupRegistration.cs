using Microsoft.Win32;

namespace Ruse.App;

/// <summary>
/// Adds or removes the current executable in the per-user Windows Run key, which
/// is what the Settings app and Task Manager show as startup apps.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ruse";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Could not determine the Ruse executable path.");
        }

        // The Run key treats the whole value as a command line, so a path with
        // spaces has to be quoted.
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
