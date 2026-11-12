using System.IO;
using System.Windows;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private TimelineService? _timeline;
    private readonly HashSet<(string NodeId, int ProcessorIndex, int ParameterIndex)> _timelineParameterChanges = new();
    private readonly object _timelineParameterLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string NodeId, int ProcessorIndex, int ParameterIndex), AudioParameter> _timelineAutomationTargets = new();
    private volatile AudioFileRecorder? _timelineRecorder;
    private string? _timelineRecordingPath;
    private volatile OutputNode? _timelineRecordingOutput;
    private volatile FormatConverter? _timelineRecordingConverter;
    private List<float> _timelineRecordingConverted = new();
    private long _timelineRecordingStartSample;
    private int _timelineRecordingLatencyFrames;
    private volatile bool _timelineRecordingActive;
    private bool _timelineBounceActive;

    private void EnsureTimeline()
    {
        if (_timeline != null)
        {
            return;
        }

        _timeline = new TimelineService();
        _timeline.ApplyAutomation = ApplyTimelineAutomation;
        _graph.Timeline = _timeline;
    }

    private void ApplyTimelineAutomation(string nodeId, int processorIndex, int parameterIndex, float value)
    {
        if (!_timelineAutomationTargets.TryGetValue((nodeId, processorIndex, parameterIndex), out var parameter))
        {
            return;
        }

        try
        {
            parameter.Set(Math.Clamp(value, parameter.Min, parameter.Max));
            lock (_timelineParameterLock)
            {
                _timelineParameterChanges.Add((nodeId, processorIndex, parameterIndex));
            }
        }
        catch
        {
        }
    }

    private void FlushTimelineParameterChanges()
    {
        (string NodeId, int ProcessorIndex, int ParameterIndex)[] changes;
        lock (_timelineParameterLock)
        {
            if (_timelineParameterChanges.Count == 0)
            {
                return;
            }

            changes = _timelineParameterChanges.ToArray();
            _timelineParameterChanges.Clear();
        }

        foreach (var change in changes)
        {
            PublishParameterChange(change.NodeId, change.ProcessorIndex, change.ParameterIndex);
        }
    }

    private void OpenTimeline()
    {
        EnsureTimeline();
        PublishTimeline(false);
    }

    private TimelineTrack AddTimelineTrack(string? name = null)
    {
        EnsureTimeline();
        var track = _timeline!.AddTrack(name);
        var source = new SourceNode(track.Name, SourceKind.Timeline);
        var entry = RegisterSource(source, 80, 160 + _timeline.Project.Tracks.Count * 120);
        _timeline.AttachSource(track.Id, source);

        var output = _nodes.Values.FirstOrDefault(item =>
            item.Output is { IsFileRecorder: false, IsNetworkOutput: false });
        if (output != null)
        {
            ConnectNodes(entry.Id, output.Id, "direct");
            SetStatus($"{track.Name}: connected to {output.Title}. Add effects on the route as usual.");
        }
        else
        {
            SetStatus($"{track.Name}: add an output and connect it before pressing play.", isError: true);
        }

        PublishTimeline(false);
        PublishGraph();
        return track;
    }

    private void AddTimelineClip(string trackId)
    {
        EnsureTimeline();
        if (string.IsNullOrWhiteSpace(trackId))
        {
            trackId = _timeline!.Project.Tracks.FirstOrDefault()?.Id ?? "";
        }

        if (string.IsNullOrWhiteSpace(trackId))
        {
            AddTimelineTrack();
            trackId = _timeline!.Project.Tracks.Last().Id;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import timeline clip",
            Filter = "Audio files (*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg)|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AddTimelineClipPath(trackId, dialog.FileName, null);
    }

    private void UpdateTimelineTrack(string trackId, float gain, bool muted, bool solo)
    {
        if (_timeline == null)
        {
            return;
        }

        if (_timeline.UpdateTrack(trackId, gain, muted, solo))
        {
            PublishTimeline(false);
        }
    }

    private void UpdateTimelineClip(BridgeMessage message)
    {
        if (_timeline == null)
        {
            return;
        }

        string trackId = String(message, "trackId");
        string clipId = String(message, "clipId");
        if (_timeline.UpdateClip(trackId, clipId,
            OptionalLong(message, "startSample"),
            OptionalLong(message, "lengthSamples"),
            OptionalLong(message, "sourceOffsetSamples"),
            OptionalFloat(message, "gain"),
            OptionalInt(message, "fadeInSamples"),
            OptionalInt(message, "fadeOutSamples")))
        {
            PublishTimeline(false);
        }
    }

    private async void StretchTimelineClip(BridgeMessage message)
    {
        if (_timeline == null)
        {
            return;
        }

        string trackId = String(message, "trackId");
        string clipId = String(message, "clipId");
        long lengthSamples = (long)Number(message, "lengthSamples");
        if (lengthSamples <= 0)
        {
            return;
        }

        string path = NextRecordingPath(AudioFileFormat.Wav24);
        SetStatus("Stretching timeline clip...");
        bool ok;
        string error;
        try
        {
            TimelineService timeline = _timeline;
            (ok, error) = await Task.Run(() =>
            {
                bool success = timeline.StretchClip(trackId, clipId, lengthSamples, path, out string message);
                return (success, message);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        if (ok)
        {
            SetStatus($"Stretched clip to {lengthSamples / (double)AudioEngineConfig.SampleRate:0.00}s.");
            PublishTimeline(false);
        }
        else
        {
            SetStatus(error, isError: true);
        }
    }

    private void AddTimelineAutomationLane(BridgeMessage message)
    {
        EnsureTimeline();
        try
        {
            _timeline!.AddAutomationLane(
                String(message, "trackId"),
                String(message, "nodeId"),
                (int)Number(message, "processorIndex"),
                (int)Number(message, "parameterIndex"),
                (float)Number(message, "value"));
            PublishTimeline(false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void SetTimelineAutomationPoints(BridgeMessage message)
    {
        if (_timeline == null)
        {
            return;
        }

        var payload = Payload<TimelineAutomationPointsPayload>(message);
        if (payload == null || !_timeline.SetAutomationPoints(payload.TrackId, payload.LaneId, payload.Points))
        {
            return;
        }

        PublishTimeline(false);
    }

    private void RemoveTimelineAutomationLane(string trackId, string laneId)
    {
        if (_timeline != null && _timeline.RemoveAutomationLane(trackId, laneId))
        {
            PublishTimeline(false);
        }
    }

    private void TimelineUndo()
    {
        if (_timeline != null && _timeline.Undo())
        {
            PublishTimeline(true);
        }
    }

    private void TimelineRedo()
    {
        if (_timeline != null && _timeline.Redo())
        {
            PublishTimeline(true);
        }
    }

    private static long? OptionalLong(BridgeMessage message, string name)
    {
        return message.Payload.TryGetProperty(name, out var value) && value.TryGetDouble(out double number)
            ? (long)number
            : null;
    }

    private static int? OptionalInt(BridgeMessage message, string name)
    {
        return message.Payload.TryGetProperty(name, out var value) && value.TryGetDouble(out double number)
            ? (int)number
            : null;
    }

    private static float? OptionalFloat(BridgeMessage message, string name)
    {
        return message.Payload.TryGetProperty(name, out var value) && value.TryGetDouble(out double number)
            ? (float)number
            : null;
    }

    private void RemoveTimelineClip(string trackId, string clipId)
    {
        if (_timeline == null)
        {
            return;
        }

        if (_timeline.RemoveClip(trackId, clipId))
        {
            PublishTimeline(false);
        }
    }

    private void MoveTimelineClip(string trackId, string clipId, long startSample)
    {
        if (_timeline == null)
        {
            return;
        }

        if (_timeline.MoveClip(trackId, clipId, startSample))
        {
            PublishTimeline(false);
        }
    }

    private void TimelinePlay()
    {
        EnsureTimeline();
        if (!_graph.Outputs.Any(output => output.IsRunning))
        {
            StartRouting();
        }

        var timelineSources = _graph.Sources.Where(source => source.Kind == SourceKind.Timeline).ToArray();
        foreach (var source in timelineSources)
        {
            EnsureTimelineSourceRoute(source);
        }

        foreach (var output in _graph.Outputs)
        {
            if (!output.IsRunning &&
                output.Inputs.Any(route => route.Source is { Kind: SourceKind.Timeline }))
            {
                try
                {
                    output.Start();
                }
                catch (Exception ex)
                {
                    SetStatus($"Could not start timeline output '{output.Title}': {ex.Message}", isError: true);
                }
            }
        }

        foreach (var source in timelineSources)
        {
            if (!source.IsRunning)
            {
                source.Start();
            }
        }

        Ruse.App.Diagnostics.AppLog.Info($"Timeline sources={timelineSources.Length}, running={timelineSources.Count(source => source.IsRunning)}.");
        _timeline!.Play();
        PublishTimeline(false);
    }

    private void TimelineStop()
    {
        StopTimelineRecording();
        _timeline?.Stop();
        PublishTimeline(false);
    }

    private void ToggleTimelineRecording()
    {
        if (_timelineRecordingActive)
        {
            StopTimelineRecording();
            _timeline?.Stop();
        }
        else
        {
            StartTimelineRecording();
        }

        PublishTimeline(false);
    }

    private void TimelineSeek(long sample)
    {
        _timeline?.Seek(sample);
        PublishTimeline(false);
    }

    private void StartTimelineRecording()
    {
        EnsureTimeline();
        if (_timeline == null || _timelineRecordingActive)
        {
            return;
        }

        var output = _nodes.Values.FirstOrDefault(item =>
            item.Output is { IsFileRecorder: false, IsNetworkOutput: false });
        if (output?.Output == null)
        {
            SetStatus("Add an output before recording into the timeline.", isError: true);
            return;
        }

        if (!_graph.Outputs.Any(item => item.IsRunning))
        {
            StartRouting();
        }

        string path = NextRecordingPath(AudioFileFormat.Wav24);
        try
        {
            _timelineRecorder = new AudioFileRecorder(path, AudioFileFormat.Wav24, AudioEngineConfig.SampleRate, 2);
        }
        catch (Exception ex)
        {
            SetStatus("Could not start timeline recording: " + ex.Message, isError: true);
            return;
        }

        _timelineRecordingPath = path;
        _timelineRecordingOutput = output.Output;
        _timelineRecordingConverter = null;
        _timelineRecordingConverted = new List<float>();
        _timelineRecordingStartSample = _timeline.PlayheadSample;
        _timelineRecordingLatencyFrames = Math.Clamp(
            (int)Math.Round(output.Output.EstimatedLatencyMs * AudioEngineConfig.SampleRate / 1000.0),
            0,
            AudioEngineConfig.SampleRate / 10);
        _timelineRecordingActive = true;
        _timelineRecordingOutput.MixObserver = OnTimelineRecordMix;
        TimelinePlay();
        SetStatus("Recording output into a new timeline track...");
        PublishTimeline(false);
    }

    private void OnTimelineRecordMix(float[] mix, int channels)
    {
        if (!_timelineRecordingActive || _timelineRecorder == null || mix.Length == 0)
        {
            return;
        }

        var recorder = _timelineRecorder;
        if (recorder == null)
        {
            return;
        }

        try
        {
            var converter = _timelineRecordingConverter;
            if (converter == null)
            {
                converter = new FormatConverter(channels, AudioEngineConfig.SampleRate);
                _timelineRecordingConverter = converter;
            }

            _timelineRecordingConverted.Clear();
            converter.Convert(mix.AsSpan(), _timelineRecordingConverted);
            recorder.Write(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_timelineRecordingConverted));
        }
        catch
        {
        }
    }

    private void AbortTimelineRecording()
    {
        if (!_timelineRecordingActive)
        {
            return;
        }

        _timelineRecordingActive = false;
        if (_timelineRecordingOutput != null)
        {
            _timelineRecordingOutput.MixObserver = null;
        }

        _timelineRecorder?.Stop();
        _timelineRecorder = null;
        _timelineRecordingConverter = null;
        _timelineRecordingPath = null;
        _timelineRecordingOutput = null;
        _timelineRecordingLatencyFrames = 0;
        _timelineRecordingConverted = new List<float>();
    }

    private void StopTimelineRecording()
    {
        if (!_timelineRecordingActive)
        {
            return;
        }

        _timelineRecordingActive = false;
        if (_timelineRecordingOutput != null)
        {
            _timelineRecordingOutput.MixObserver = null;
        }

        var recorder = _timelineRecorder;
        recorder?.Stop(TimeSpan.FromSeconds(10));
        _timelineRecorder = null;
        _timelineRecordingConverter = null;
        _timelineRecordingConverted = new List<float>();

        string path = _timelineRecordingPath ?? "";
        long start = _timelineRecordingStartSample;
        _timelineRecordingPath = null;
        _timelineRecordingOutput = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            new FileInfo(path).Length <= 44)
        {
            SetStatus(recorder?.LastError ?? "The timeline recording could not be saved.", isError: true);
            return;
        }

        if (recorder?.LastError != null)
        {
            SetStatus($"Recording saved with a warning: {recorder.LastError}");
        }

        if (_timeline == null)
        {
            return;
        }

        var track = AddTimelineTrack("Take");
        if (!_timeline.AddClip(track.Id, path, out string error))
        {
            SetStatus(error, isError: true);
            return;
        }

        var clip = _timeline.Project.Tracks.FirstOrDefault(item => item.Id == track.Id)?.Clips.LastOrDefault();
        if (clip != null)
        {
            int trim = Math.Min(_timelineRecordingLatencyFrames,
                (int)Math.Max(0, clip.LengthSamples - 2));
            if (trim > 0)
            {
                _timeline.UpdateClip(track.Id, clip.Id, null,
                    clip.LengthSamples - trim, trim, null, null, null);
            }

            _timeline.MoveClip(track.Id, clip.Id, start);
        }

        _timelineRecordingLatencyFrames = 0;

        _timeline.SetTrackSolo(track.Id, true);
        PublishTimeline(true);
        PublishGraph();
        SetStatus($"Recorded {Path.GetFileName(path)} into a new timeline track.");
    }

    private async void BounceTimeline()
    {
        EnsureTimeline();
        if (_timeline == null)
        {
            return;
        }

        if (_timelineBounceActive)
        {
            SetStatus("A timeline bounce is already running.", isError: true);
            return;
        }

        if (_timelineRecordingActive)
        {
            SetStatus("Stop timeline recording before bouncing.", isError: true);
            return;
        }

        if (_timeline.TotalLengthSamples() <= 0)
        {
            SetStatus("The timeline has no audio to bounce.", isError: true);
            return;
        }

        string recordingDirectory = RecordingDirectory();
        Directory.CreateDirectory(recordingDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Bounce timeline mix",
            Filter = "WAV audio (*.wav)|*.wav",
            DefaultExt = "wav",
            AddExtension = true,
            FileName = "ruse-timeline-bounce.wav",
            InitialDirectory = recordingDirectory,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        _timelineBounceActive = true;
        SetStatus("Bouncing timeline...");
        bool ok;
        string error;
        try
        {
            TimelineService timeline = _timeline;
            (ok, error) = await Task.Run(() =>
            {
                bool success = timeline.BounceToWave(path, out string message);
                return (success, message);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        _timelineBounceActive = false;
        if (_disposed)
        {
            return;
        }

        if (ok)
        {
            SetStatus($"Timeline bounced to {path}.");
        }
        else
        {
            SetStatus(error, isError: true);
        }
    }

    private async void FreezeTimeline()
    {
        EnsureTimeline();
        if (_timelineBounceActive)
        {
            SetStatus("A timeline bounce is already running.", isError: true);
            return;
        }

        if (_timelineRecordingActive)
        {
            SetStatus("Stop timeline recording before freezing.", isError: true);
            return;
        }

        if (_timeline == null || _timeline.TotalLengthSamples() <= 0)
        {
            SetStatus("The timeline has no audio to freeze.", isError: true);
            return;
        }

        _timelineBounceActive = true;
        string path = NextRecordingPath(AudioFileFormat.Wav24);
        SetStatus("Freezing timeline...");
        bool ok;
        string error;
        try
        {
            TimelineService timeline = _timeline;
            (ok, error) = await Task.Run(() =>
            {
                bool success = timeline.BounceToWave(path, out string message);
                return (success, message);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        if (!ok)
        {
            _timelineBounceActive = false;
            SetStatus(error, isError: true);
            return;
        }

        if (_disposed)
        {
            _timelineBounceActive = false;
            return;
        }

        TimelineTrack track;
        try
        {
            track = AddTimelineTrack("Freeze");
        }
        catch (Exception ex)
        {
            _timelineBounceActive = false;
            SetStatus("Could not create the freeze track: " + ex.Message, isError: true);
            return;
        }

        if (!_timeline.AddClip(track.Id, path, out string importError))
        {
            _timelineBounceActive = false;
            SetStatus(importError, isError: true);
            return;
        }

        _timeline.SetTrackSolo(track.Id, true);
        PublishTimeline(true);
        PublishGraph();
        _timelineBounceActive = false;
        SetStatus($"Timeline frozen to {Path.GetFileName(path)}. Original tracks are preserved but muted by solo.");
    }

    private void TimelineSetTempo(double tempo)
    {
        if (_timeline != null && _timeline.SetTempo(tempo))
        {
            PublishTimeline(false);
        }
    }

    private void TimelineSetLoop(long? startSample, long? endSample)
    {
        _timeline?.SetLoop(startSample, endSample);
        PublishTimeline(false);
    }

    private void PublishTimeline(bool includePeaks = true)
    {
        RefreshTimelineAutomationTargets();
        if (_timeline == null)
        {
            Post("timeline", new
            {
                project = new TimelineProject(),
                playheadSample = 0L,
                playing = false,
                sampleRate = AudioEngineConfig.SampleRate,
                canUndo = false,
                canRedo = false,
                recording = false,
            });
            return;
        }

        Post("timeline", new
        {
            project = _timeline.SnapshotProject(includePeaks),
            playheadSample = _timeline.PlayheadSample,
            playing = _timeline.IsPlaying,
            sampleRate = _timeline.SampleRate,
            canUndo = _timeline.CanUndo,
            canRedo = _timeline.CanRedo,
            recording = _timelineRecordingActive,
        });
    }

    private void PublishTimelinePlayhead()
    {
        if (_timeline == null)
        {
            return;
        }

        Post("timeline-playhead", new
        {
            playheadSample = _timeline.PlayheadSample,
            playing = _timeline.IsPlaying,
        });
    }
}






internal sealed class TimelineAutomationPointsPayload
{
    public string TrackId { get; set; } = "";
    public string LaneId { get; set; } = "";
    public List<TimelineAutomationPoint> Points { get; set; } = new();
}
