using System.IO;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    internal bool HasActiveRecording =>
        _nodes.Values.Any(entry => entry.Output?.IsRecording == true);

    private static string RecordingDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppSettings.Directory;
        }

        return Path.Combine(root, "Ruse Recordings");
    }

    private static string NextRecordingPath(AudioFileFormat format)
    {
        string directory = RecordingDirectory();
        Directory.CreateDirectory(directory);
        string extension = AudioFileFormats.Get(format).Extension;
        string stem = "ruse-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string path = Path.Combine(directory, stem + "." + extension);
        for (int suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{stem}-{suffix}.{extension}");
        }

        return path;
    }

    private bool StartRecorder(NodeEntry entry, OutputNode output)
    {
        if (output.IsRecording)
        {
            return true;
        }

        output.AutoStart = true;

        if (output.AutoSave)
        {
            output.SetRecordingDestination(NextRecordingPath(output.RecordingFormat), output.RecordingFormat);
        }
        else if (!output.HasRecordingPath)
        {
            SetStatus("Choose a recording file or enable auto save first.", isError: true);
            return false;
        }

        output.Start();
        if (output.IsRecording)
        {
            entry.Title = output.Title;
            return true;
        }

        if (!string.IsNullOrEmpty(output.RecordingError))
        {
            SetStatus(output.RecordingError, isError: true);
        }

        return false;
    }

    private void CheckArmedRecorders()
    {
        if (!_graph.AutoRun)
        {
            return;
        }

        bool changed = false;
        foreach (var entry in _nodes.Values)
        {
            var output = entry.Output;
            if (output is not { IsFileRecorder: true, IsRecording: false, RecorderMode: RecorderMode.Armed, AutoStart: true } ||
                (!output.AutoSave && !output.HasRecordingPath))
            {
                continue;
            }

            bool hasAudio = output.Inputs.Any(route =>
                route.Source != null && route.Source.MeterLevel > 0.01f);
            if (!hasAudio)
            {
                continue;
            }

            if (StartRecorder(entry, output))
            {
                changed = true;
            }
        }

        if (changed)
        {
            _running = _graph.Outputs.Any(output => output.IsRunning);
            SetStatus("Armed recorder started.");
            PublishGraph();
            PublishTelemetry(force: true);
        }
    }

    private void SetRecorderMode(string id, string modeName)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Output is not { IsFileRecorder: true } output ||
            !Enum.TryParse<RecorderMode>(modeName, true, out var mode))
        {
            return;
        }

        if (output.IsRecording && mode != RecorderMode.On)
        {
            output.Stop();
            _running = _graph.Outputs.Any(item => item.IsRunning);
            SetStatus("Recording saved.");
        }

        output.AutoStart = true;
        output.RecorderMode = mode;
        if (mode == RecorderMode.On)
        {
            if (_graph.AutoRun)
            {
                if (StartRecorder(entry, output)) SetStatus("Recording started.");
            }
            else
            {
                SetStatus("Recorder is on; start routing to record.");
            }
        }
        else if (mode == RecorderMode.Armed)
        {
            SetStatus(_graph.AutoRun
                ? "Recorder armed; waiting for audio."
                : "Recorder armed; start routing when ready.");
        }
        else
        {
            SetStatus("Recorder off.");
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetRecorderAutoSave(string id, bool value)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsFileRecorder: true } output)
        {
            return;
        }

        if (output.IsRecording)
        {
            SetStatus("Stop recording before changing auto save.", isError: true);
            return;
        }

        output.AutoSave = value;
        if (value && output.RecorderMode == RecorderMode.On && _graph.AutoRun)
        {
            StartRecorder(entry, output);
        }

        SetStatus(value
            ? "Auto save on; files use the recording date and time."
            : "Auto save off.");
        PublishGraph();
    }
}
