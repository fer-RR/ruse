using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Ruse.App.Diagnostics;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Application log. Everything that goes wrong ends up in
/// %APPDATA%\Ruse\ruse.log, and anything critical is also surfaced in the
/// window so the user is told instead of silently getting no audio.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    /// <summary>Raised for every entry so the UI can react to critical ones.</summary>
    public static event Action<LogLevel, string>? Entry;

    public static string LogPath { get; } = Path.Combine(AppSettings.Directory, "ruse.log");

    /// <summary>Number of errors since the app started (shown in the status bar).</summary>
    public static int ErrorCount { get; private set; }

    /// <summary>Installs the global exception handlers.</summary>
    public static void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Error("Unhandled exception on a background thread.", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        Info("Ruse started.");
    }

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warning(string message) => Write(LogLevel.Warning, message, null);

    public static void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, message, exception);

    private static void Write(LogLevel level, string message, Exception? exception)
    {
        string line = Format(level, message, exception);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                Rotate();
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Never let logging break the app.
            }

            if (level == LogLevel.Error)
            {
                ErrorCount++;
            }
        }

        Debug.WriteLine(line);
        Entry?.Invoke(level, message);
    }

    private static string Format(LogLevel level, string message, Exception? exception)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string text = $"{stamp} [{level.ToString().ToUpperInvariant()}] {message}";

        if (exception != null)
        {
            text += Environment.NewLine + "    " +
                exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");
        }

        return text;
    }

    /// <summary>Keeps the log bounded: one previous file is kept.</summary>
    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            string old = LogPath + ".1";
            if (File.Exists(old))
            {
                File.Delete(old);
            }

            File.Move(LogPath, old);
        }
        catch
        {
        }
    }

    /// <summary>Opens the log file in the default viewer.</summary>
    public static void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (!File.Exists(LogPath))
            {
                File.WriteAllText(LogPath, string.Empty);
            }

            Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warning("Could not open the log file: " + ex.Message);
        }
    }

    /// <summary>Opens the folder that holds the log and the user's settings.</summary>
    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            Process.Start(new ProcessStartInfo(AppSettings.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warning("Could not open the log folder: " + ex.Message);
        }
    }

    /// <summary>The most recent entries, newest last.</summary>
    public static IReadOnlyList<string> Recent(int count)
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return Array.Empty<string>();
            }

            var lines = File.ReadAllLines(LogPath);
            int start = Math.Max(0, lines.Length - count);
            var result = new List<string>();
            for (int i = start; i < lines.Length; i++)
            {
                result.Add(lines[i]);
            }

            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
