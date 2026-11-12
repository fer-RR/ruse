using System.Diagnostics;
using System.IO;

namespace Ruse.App;

internal static class UpdateApplier
{
    public static int Run(string[] args)
    {
        string logPath = Arg(args, "--log") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ruse", "update.log");

        void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(
                    logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        string? restart = null;
        try
        {
            Log("Update helper started.");
            int pid = int.TryParse(Arg(args, "--pid"), out int parsed) ? parsed : 0;
            string? targetArg = Arg(args, "--target");
            string? sourceArg = Arg(args, "--source");
            string? restartArg = Arg(args, "--restart");
            if (string.IsNullOrWhiteSpace(targetArg) ||
                string.IsNullOrWhiteSpace(sourceArg) ||
                string.IsNullOrWhiteSpace(restartArg))
            {
                Log("Missing update paths.");
                return 2;
            }

            string target = Normalize(targetArg);
            string source = Normalize(sourceArg);
            restart = Normalize(restartArg);
            if (!IsSubPath(restart, target))
            {
                restart = Path.Combine(target, "Ruse.exe");
            }

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
                IsSubPath(source, target) ||
                IsSubPath(target, source) ||
                !File.Exists(Path.Combine(source, "Ruse.exe")))
            {
                Log("The staging or target folder is not valid.");
                return 2;
            }

            if (pid > 0)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (!process.WaitForExit(120_000))
                    {
                        Log("Timed out waiting for Ruse to close.");
                        TryStart(restart, target, Log);
                        return 4;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            Thread.Sleep(2000);
            foreach (Process process in Process.GetProcessesByName("Ruse.TtsHost"))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            string backup = target + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            bool moved = false;
            for (int attempt = 0; attempt < 10 && !moved; attempt++)
            {
                try
                {
                    Directory.Move(target, backup);
                    moved = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(750);
                }
            }

            if (!moved)
            {
                Log("Could not move the current Ruse folder. Close any remaining audio processes and try again.");
                TryStart(restart, target, Log);
                return 3;
            }

            Log($"Backed up the current install to {backup}.");
            bool copied = false;
            try
            {
                Directory.CreateDirectory(target);
                CopyDirectory(source, target, Log);
                copied = true;
            }
            catch (Exception ex)
            {
                Log("Copying the update failed: " + ex);
            }

            if (!copied)
            {
                Rollback(target, backup, restart, null, Log);
                return 5;
            }

            Process? child = null;
            try
            {
                child = Process.Start(new ProcessStartInfo
                {
                    FileName = restart,
                    WorkingDirectory = target,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Log("Could not start the updated Ruse: " + ex);
            }

            if (child == null)
            {
                Rollback(target, backup, restart, null, Log);
                return 6;
            }

            Thread.Sleep(8000);
            child.Refresh();
            if (child.HasExited)
            {
                Log($"The updated Ruse exited with code {child.ExitCode}.");
                Rollback(target, backup, restart, child, Log);
                return 7;
            }

            Log("Update succeeded. The previous version is kept at " + backup + ".");
            return 0;
        }
        catch (Exception ex)
        {
            Log("Update failed: " + ex);
            if (!string.IsNullOrWhiteSpace(restart))
            {
                TryStart(restart, Path.GetDirectoryName(restart) ?? "", Log);
            }

            return 9;
        }
    }

    private static void Rollback(
        string target,
        string backup,
        string restart,
        Process? child,
        Action<string> log)
    {
        try
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log("Could not remove the failed update: " + ex);
        }

        try
        {
            if (Directory.Exists(backup) && !Directory.Exists(target))
            {
                Directory.Move(backup, target);
            }
        }
        catch (Exception ex)
        {
            log("Could not restore the previous version: " + ex);
        }

        TryStart(restart, target, log);
    }

    private static void TryStart(string executable, string workingDirectory, Action<string> log)
    {
        try
        {
            if (File.Exists(executable))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            log("Could not restart Ruse: " + ex);
        }
    }

    private static void CopyDirectory(string source, string target, Action<string> log)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            bool copied = false;
            for (int attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    File.Copy(file, destination, overwrite: true);
                    copied = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(300);
                }
            }

            if (!copied)
            {
                throw new IOException("Could not copy " + file);
            }
        }

        log($"Copied {Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length} files.");
    }

    private static string? Arg(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static bool IsSubPath(string path, string parent)
    {
        string relative = Path.GetRelativePath(parent, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}
