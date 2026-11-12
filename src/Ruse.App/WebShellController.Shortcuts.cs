using System;
using System.IO;
using System.Reflection;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void CreateDesktopShortcut()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                throw new DirectoryNotFoundException("The desktop folder could not be found.");
            }

            string executable = Path.Combine(AppContext.BaseDirectory, "Ruse.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Ruse.exe was not found.", executable);
            }

            string shortcutPath = Path.Combine(desktop, "Ruse.lnk");
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("Windows Script Host is unavailable.");
            }

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                throw new InvalidOperationException("Windows Script Host could not be started.");
            }

            object? shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });
            if (shortcut == null)
            {
                throw new InvalidOperationException("The shortcut could not be created.");
            }

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember(
                "TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
            shortcutType.InvokeMember(
                "WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppContext.BaseDirectory });
            shortcutType.InvokeMember(
                "IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
            shortcutType.InvokeMember(
                "Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Ruse audio router" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            SetStatus("Desktop shortcut created.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not create the desktop shortcut: " + ex.Message, isError: true);
        }
    }
}
