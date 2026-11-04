using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Ruse.App;

internal sealed class TtsRequest
{
    public string Family { get; set; } = "";
    public string ModelDir { get; set; } = "";
    public string Text { get; set; } = "";
    public float Speed { get; set; } = 1f;
    public int Voice { get; set; }
    public string OutputPath { get; set; } = "";
}

/// <summary>
/// Runs the isolated sherpa-onnx host. Keeping synthesis in a child process
/// avoids loading a second ONNX Runtime into the app and keeps model loading
/// off the UI thread.
/// </summary>
internal static class TtsSynthesis
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static async Task<string> SynthesizeAsync(
        string modelId,
        string text,
        float speed,
        int voice,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("There is no text to speak.");
        }

        var model = TtsModels.Find(modelId) ?? throw new InvalidOperationException("Choose a text-to-speech model first.");
        if (!TtsModels.IsInstalled(model.Id))
        {
            throw new InvalidOperationException("Install a text-to-speech model in Settings > Speech & translation first.");
        }

        string host = Path.Combine(AppContext.BaseDirectory, "tts-host", "Ruse.TtsHost.exe");
        if (!File.Exists(host))
        {
            host = Path.Combine(AppContext.BaseDirectory, "Ruse.TtsHost.exe");
        }

        if (!File.Exists(host))
        {
            throw new FileNotFoundException("The text-to-speech host is missing from this build.", host);
        }

        string folder = Path.Combine(Path.GetTempPath(), "Ruse", "tts");
        Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(folder, id + ".json");
        string outputPath = Path.Combine(folder, id + ".wav");
        var request = new TtsRequest
        {
            Family = model.Family,
            ModelDir = TtsModels.GetInstalledPath(model.Id),
            Text = text.Trim(),
            Speed = Math.Clamp(speed <= 0 ? 1f : speed, 0.5f, 2f),
            Voice = Math.Max(0, voice),
            OutputPath = outputPath,
        };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), cancellationToken)
            .ConfigureAwait(false);

        var info = new ProcessStartInfo
        {
            FileName = host,
            Arguments = "--request " + Quote(requestPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(host)!,
        };

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The text-to-speech host did not start.");
            }

            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string error = (await stderr.ConfigureAwait(false)).Trim();
            _ = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "The text-to-speech model could not generate audio."
                        : error);
            }

            return outputPath;
        }
        finally
        {
            try
            {
                File.Delete(requestPath);
            }
            catch
            {
            }
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
