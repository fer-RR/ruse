using System.IO;
using System.Windows;
using NAudio.Wave;
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
    private CancellationTokenSource? _timelineBounceCancel;
    private SourceNode? _timelineMetronomeSource;
    private bool _timelineMetronome;
    private bool _timelineCountInActive;
    private bool _timelineCountInWasMetronome;
    private int _timelineCountInBeats = 4;
    private long _lastSourceStatePublish;
    private CancellationTokenSource? _timelineCountInCancel;

    private void EnsureTimeline()
    {
        if (_timeline != null)
        {
            return;
        }

        _timeline = new TimelineService();
        _timeline.ApplyAutomation = ApplyTimelineAutomation;
        _timeline.ApplyGeneratorGate = ApplyGeneratorGate;
        _graph.Timeline = _timeline;
    }

    private void ApplyGeneratorGate(string nodeId, float gain)
    {
        if (_nodes.TryGetValue(nodeId, out var entry) && entry.Source != null)
        {
            entry.Source.TimelineGate = Math.Clamp(gain, 0f, 1f);
        }
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
            if (processorIndex == -1 && Environment.TickCount64 - _lastSourceStatePublish >= 100)
            {
                _lastSourceStatePublish = Environment.TickCount64;
                PublishSourceState(nodeId);
            }
            lock (_timelineParameterLock)
            {
                _timelineParameterChanges.Add((nodeId, processorIndex, parameterIndex));
            }
        }
        catch
        {
        }
    }

    private void PublishSourceState(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { } source)
        {
            return;
        }

        Post("source-state", new SourceStateDto
        {
            Id = id,
            ToneFrequency = source.ToneFrequency,
            ToneWaveform = source.ToneWave.ToString(),
            ToneLevelDb = source.ToneLevelDb,
            MetronomeBpm = source.MetronomeBpm,
            MetronomeBeatsPerBar = source.MetronomeBeatsPerBar,
            SynthFrequency = source.Synth.Frequency,
            SynthWaveform = source.Synth.Waveform.ToString(),
            SynthLevelDb = source.Synth.LevelDb,
            SynthHarmonics = source.Synth.Harmonics,
            SynthDetuneCents = source.Synth.DetuneCents,
            SynthNoise = source.Synth.Noise,
            SynthCutoffHz = source.Synth.CutoffHz,
            SynthResonance = source.Synth.Resonance,
            SynthMovement = source.Synth.Movement,
            SynthLfoRateHz = source.Synth.LfoRateHz,
            SynthStereoWidth = source.Synth.StereoWidth,
        });
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
            string stretched = $"Stretched clip to {lengthSamples / (double)AudioEngineConfig.SampleRate:0.00}s.";
            SetStatus(stretched);
            ShowToast(stretched);
            PublishTimeline(false);
        }
        else
        {
            SetStatus(error, isError: true);
            ShowToast(error, isError: true);
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

    private void SplitTimelineClip(string trackId, string clipId, long sample)
    {
        if (_timeline?.SplitClip(trackId, clipId, sample) == true)
        {
            PublishTimeline(false);
        }
        else
        {
            SetStatus("Place the playhead inside the clip before splitting.", isError: true);
        }
    }

    private void DuplicateTimelineClip(string trackId, string clipId)
    {
        if (_timeline?.DuplicateClip(trackId, clipId) == true)
        {
            PublishTimeline(false);
        }
    }

    private void CrossfadeTimelineClip(string trackId, string clipId)
    {
        if (_timeline?.CrossfadeClip(trackId, clipId) == true)
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

    private void MoveTimelineClipToTrack(string fromTrackId, string toTrackId,
        string clipId, long startSample)
    {
        if (_timeline == null)
        {
            return;
        }

        if (_timeline.MoveClipToTrack(fromTrackId, toTrackId, clipId, startSample))
        {
            PublishTimeline(false);
        }
    }

    private void AddTimelineGenerator(string nodeId, string trackId, long startSample)
    {
        if (!_nodes.TryGetValue(nodeId, out var entry) || entry.Source is not { } source ||
            (!source.IsMetronome && source.Kind is not (SourceKind.Tone or SourceKind.Synth)))
        {
            SetStatus("Drag a tone, synth, or metronome node to the timeline.", isError: true);
            return;
        }

        EnsureTimeline();
        if (_timeline == null) return;
        var track = _timeline.Project.Tracks.FirstOrDefault(item => item.GeneratorNodeId == nodeId);
        if (track == null)
        {
            track = _timeline.AddGeneratorTrack(nodeId, source.Title);
        }

        if (!_timeline.AddGeneratorClip(track.Id, startSample, AudioEngineConfig.SampleRate * 4, source.Title, out string error))
        {
            SetStatus(error, isError: true);
            return;
        }

        source.TimelineGate = 0f;
        if (!_links.Any(link => link.From == nodeId && link.Enabled))
        {
            var output = _nodes.Values.FirstOrDefault(item =>
                item.Output is { IsFileRecorder: false, IsNetworkOutput: false });
            if (output != null)
            {
                ConnectEntries(entry, output, publish: false);
            }
        }

        if (!source.IsRunning) source.Start();
        PublishTimeline(true);
        PublishGraph();
        SetStatus($"Added {source.Title} to the timeline. Drag the right edge or edit Length (ms) to set its duration.");
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

        foreach (var track in _timeline!.Project.Tracks.Where(track => !string.IsNullOrWhiteSpace(track.GeneratorNodeId)))
        {
            if (_nodes.TryGetValue(track.GeneratorNodeId!, out var generator) &&
                generator.Source != null && !generator.Source.IsRunning)
            {
                generator.Source.Start();
            }
        }

        Ruse.App.Diagnostics.AppLog.Info($"Timeline sources={timelineSources.Length}, running={timelineSources.Count(source => source.IsRunning)}.");
        _timeline!.Play();
        PublishTimeline(false);
    }

    private void TimelineStop()
    {
        CancelTimelineCountIn();
        StopTimelineRecording();
        if (_timeline != null)
        {
            foreach (var track in _timeline.Project.Tracks.Where(track => !string.IsNullOrWhiteSpace(track.GeneratorNodeId)))
            {
                ApplyGeneratorGate(track.GeneratorNodeId!, 0f);
            }
        }

        _timeline?.Stop();
        PublishTimeline(false);
    }

    private void SetTimelineMetronome(bool enabled)
    {
        if (enabled == _timelineMetronome)
        {
            PublishTimeline(false);
            return;
        }

        if (!enabled)
        {
            StopTimelineMetronome();
            PublishTimeline(false);
            return;
        }

        if (_timeline == null)
        {
            return;
        }

        var outputs = _graph.Outputs
            .Where(output => !output.IsFileRecorder && !output.IsNetworkOutput)
            .ToArray();
        if (outputs.Length == 0)
        {
            SetStatus("Add an output before enabling the metronome.", isError: true);
            return;
        }

        var source = new SourceNode("Metronome", SourceKind.Tone);
        source.SetMetronome((float)(_timeline.Project.Tempo > 0 ? _timeline.Project.Tempo : 120), 4);
        _graph.AddSource(source);
        foreach (var output in outputs)
        {
            _graph.Connect(source, output, 1f);
        }

        source.Start();
        _timelineMetronomeSource = source;
        _timelineMetronome = true;
        PublishTimeline(false);
    }

    private void StopTimelineMetronome()
    {
        var source = _timelineMetronomeSource;
        _timelineMetronomeSource = null;
        _timelineMetronome = false;
        if (source == null)
        {
            return;
        }

        foreach (var output in _graph.Outputs)
        {
            _graph.Disconnect(source, output);
        }

        source.Stop();
        source.Dispose();
        _graph.RemoveSource(source, invalidateEngine: true);
    }

    private void CancelTimelineCountIn()
    {
        if (!_timelineCountInActive)
        {
            return;
        }

        _timelineCountInCancel?.Cancel();
        _timelineCountInActive = false;
        if (!_timelineCountInWasMetronome)
        {
            StopTimelineMetronome();
        }
    }

    private void SetTimelineCountInBeats(int beats)
    {
        _timelineCountInBeats = Math.Clamp(beats, 1, 16);
        PublishTimeline(false);
    }

    private async void TimelineCountIn()
    {
        EnsureTimeline();
        if (_timeline == null || _timeline.IsPlaying || _timelineCountInActive)
        {
            return;
        }

        bool wasMetronome = _timelineMetronome;
        if (!wasMetronome)
        {
            SetTimelineMetronome(true);
            if (!_timelineMetronome)
            {
                return;
            }
        }

        _timelineCountInActive = true;
        _timelineCountInWasMetronome = wasMetronome;
        var cancel = new CancellationTokenSource();
        _timelineCountInCancel = cancel;
        double bpm = _timeline.Project.Tempo > 0 ? _timeline.Project.Tempo : 120;
        int beats = Math.Clamp(_timelineCountInBeats, 1, 16);
        SetStatus($"Count-in: {beats} beats...");
        PublishTimeline(false);
        try
        {
            await Task.Delay((int)Math.Round(beats * 60_000.0 / bpm), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_timelineCountInCancel, cancel))
            {
                _timelineCountInCancel = null;
            }

            cancel.Dispose();
        }

        if (!_timelineCountInActive)
        {
            return;
        }

        _timelineCountInActive = false;
        if (!_timelineMetronome)
        {
            return;
        }

        TimelinePlay();
        if (!wasMetronome)
        {
            StopTimelineMetronome();
            PublishTimeline(false);
        }
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

    private void EndTimelineBounce()
    {
        _timelineBounceActive = false;
        _timelineBounceCancel?.Dispose();
        _timelineBounceCancel = null;
        Post("timeline-bounce-progress", new { active = false, progress = 1.0 });
    }

    private void CancelTimelineBounce()
    {
        if (!_timelineBounceActive) return;
        _timelineBounceCancel?.Cancel();
        SetStatus("Canceling timeline render...");
    }

    private async void BounceTimeline() => await BounceTimelineCore(rangeOnly: false);

    private async void BounceTimelineRange() => await BounceTimelineCore(rangeOnly: true);

    private async Task BounceTimelineCore(bool rangeOnly)
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

        long rangeStart = 0;
        long rangeEnd = -1;
        if (rangeOnly)
        {
            rangeStart = _timeline.Project.LoopStartSample;
            rangeEnd = _timeline.Project.LoopEndSample;
            if (rangeStart < 0 || rangeEnd <= rangeStart)
            {
                SetStatus("Set a loop range before bouncing the timeline.", isError: true);
                return;
            }
        }

        string recordingDirectory = RecordingDirectory();
        Directory.CreateDirectory(recordingDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = rangeOnly ? "Bounce timeline range" : "Bounce timeline mix",
            Filter = "WAV audio (*.wav)|*.wav",
            DefaultExt = "wav",
            AddExtension = true,
            FileName = rangeOnly ? "ruse-timeline-range.wav" : "ruse-timeline-bounce.wav",
            InitialDirectory = recordingDirectory,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        _timelineBounceActive = true;
        SetStatus(rangeOnly ? "Bouncing timeline range..." : "Bouncing timeline...");
        bool ok;
        string error;
        OfflineTimelineBounce? bounce;
        try
        {
            bounce = BuildTimelineBounceGraph();
        }
        catch (Exception ex)
        {
        EndTimelineBounce();
            SetStatus("Could not prepare the timeline bounce: " + ex.Message, isError: true);
            return;
        }

        if (bounce == null)
        {
        EndTimelineBounce();
            SetStatus("Add an output device before bouncing the timeline.", isError: true);
            return;
        }

        bounce.StartSample = rangeStart;
        var bounceCancel = new CancellationTokenSource();
        _timelineBounceCancel = bounceCancel;
        bounce.Progress = progress => Post("timeline-bounce-progress", new { active = true, progress });
        bounce.CancelToken = bounceCancel.Token;
        Post("timeline-bounce-progress", new { active = true, progress = 0.0 });

        bounce.EndSample = rangeEnd;
        using (bounce)
        {
            try
            {
                (ok, error) = await Task.Run(() =>
                {
                    bool success = bounce.Render(path, out string message);
                    return (success, message);
                });
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
            }
        }

        EndTimelineBounce();
        if (_disposed)
        {
            return;
        }

        if (ok)
        {
            string message = $"{(rangeOnly ? "Timeline range" : "Timeline")} bounced to {path}." +
                (bounce.SkippedVst3 ? " VST3 effects were skipped in the offline bounce." : "") +
                (bounce.SkippedProcessor ? " Some effects could not be cloned and were skipped." : "");
            SetStatus(message);
            ShowToast(message);
        }
        else
        {
            SetStatus(error, isError: true);
            ShowToast(error, isError: true);
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
        OfflineTimelineBounce? bounce;
        try
        {
            bounce = BuildTimelineBounceGraph();
        }
        catch (Exception ex)
        {
        EndTimelineBounce();
            SetStatus("Could not prepare the timeline freeze: " + ex.Message, isError: true);
            return;
        }

        if (bounce == null)
        {
        EndTimelineBounce();
            SetStatus("Add an output device before freezing the timeline.", isError: true);
            return;
        }

        var freezeCancel = new CancellationTokenSource();
        _timelineBounceCancel = freezeCancel;
        bounce.Progress = progress => Post("timeline-bounce-progress", new { active = true, progress });
        bounce.CancelToken = freezeCancel.Token;
        Post("timeline-bounce-progress", new { active = true, progress = 0.0 });

        using (bounce)
        {
            try
            {
                (ok, error) = await Task.Run(() =>
                {
                    bool success = bounce.Render(path, out string message);
                    return (success, message);
                });
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
            }
        }

        if (!ok)
        {
        EndTimelineBounce();
            SetStatus(error, isError: true);
            return;
        }

        if (_disposed)
        if (_disposed)
        {
        EndTimelineBounce();
            return;
        }

        TimelineTrack track;
        try
        {
            track = AddTimelineTrack("Freeze");
        }
        catch (Exception ex)
        {
        EndTimelineBounce();
            SetStatus("Could not create the freeze track: " + ex.Message, isError: true);
            return;
        }

        if (!_timeline.AddClip(track.Id, path, out string importError))
        {
        EndTimelineBounce();
            SetStatus(importError, isError: true);
            return;
        }

        _timeline.SetTrackSolo(track.Id, true);
        PublishTimeline(true);
        PublishGraph();
        EndTimelineBounce();
        string frozen = $"Timeline frozen to {Path.GetFileName(path)}. Original tracks are preserved but muted by solo." +
            (bounce.SkippedVst3 ? " VST3 effects were skipped in the offline freeze." : "") +
            (bounce.SkippedProcessor ? " Some effects could not be cloned and were skipped." : "");
        SetStatus(frozen);
        ShowToast(frozen);
    }

    private void TimelineSetTempo(double tempo)
    {
        if (_timeline != null && _timeline.SetTempo(tempo))
        {
            _timelineMetronomeSource?.SetMetronome((float)tempo, 4);
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
                metronome = false,
                countInBeats = _timelineCountInBeats,
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
            metronome = _timelineMetronome,
            countInBeats = _timelineCountInBeats,
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
