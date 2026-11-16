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
        if (_nodes.TryGetValue(nodeId, out var entry) &&
            entry.Source is { } source &&
            (source.IsMetronome || source.Kind is SourceKind.Tone or SourceKind.Synth))
        {
            source.TimelineGate = Math.Clamp(gain, 0f, 1f);
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
            OptionalInt(message, "fadeOutSamples"),
            OptionalFloat(message, "pitchSemitones")))
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
            RegisterGeneratedTimelineFile(path);
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
            SyncTimelineMetronome();
            SyncTimelineTrackNames();
            PublishTimeline(true);
            PublishGraph();
            PruneGeneratedTimelineFiles();
        }
    }

    private void TimelineRedo()
    {
        if (_timeline != null && _timeline.Redo())
        {
            SyncTimelineMetronome();
            SyncTimelineTrackNames();
            PublishTimeline(true);
            PublishGraph();
            PruneGeneratedTimelineFiles();
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
            PruneGeneratedTimelineFiles();
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
        if (_timeline == null)
        {
            return;
        }

        foreach (var output in _graph.Outputs)
        {
            DisableNativeOnSharedOutput(output);
        }

        _graph.Timeline = _timeline;
        if (!_graph.Outputs.Any(output => output.IsRunning))
        {
            StartRouting();
        }
        else
        {
            _graph.StartEngine();
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

        Ruse.App.Diagnostics.AppLog.Info(
            $"Timeline sources={timelineSources.Length}, running={timelineSources.Count(source => source.IsRunning)}.");
        _timeline!.Play();
        PublishTimeline(false);
    }

    private void TimelinePause()
    {
        if (_timeline == null)
        {
            return;
        }

        foreach (var track in _timeline.Project.Tracks.Where(track => !string.IsNullOrWhiteSpace(track.GeneratorNodeId)))
        {
            ApplyGeneratorGate(track.GeneratorNodeId!, 0f);
        }

        _timeline.Stop();
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

            _timeline.Stop();
            _timeline.Seek(0);
        }

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
        source.SetMetronome(
            TimelineMetronomeTempo(),
            _timeline.Project.TimeSignatureNumerator);
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
        double bpm = TimelineMetronomeTempo();
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
            TryDeleteTimelineFile(path);
            SetStatus(importError, isError: true);
            return;
        }

        RegisterGeneratedTimelineFile(path);
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

    private float TimelineMetronomeTempo()
    {
        if (_timeline == null)
        {
            return 120f;
        }

        double tempo = _timeline.Project.Tempo > 0 ? _timeline.Project.Tempo : 120;
        int denominator = _timeline.Project.TimeSignatureDenominator is 1 or 2 or 4 or 8 or 16
            ? _timeline.Project.TimeSignatureDenominator
            : 4;
        return (float)(tempo * denominator / 4.0);
    }

    private void SyncTimelineMetronome()
    {
        if (_timeline == null || _timelineMetronomeSource == null)
        {
            return;
        }

        _timelineMetronomeSource.SetMetronome(
            TimelineMetronomeTempo(),
            _timeline.Project.TimeSignatureNumerator);
    }

    private void TimelineSetTempo(double tempo)
    {
        if (_timeline != null && _timeline.SetTempo(tempo))
        {
            _timelineMetronomeSource?.SetMetronome(
                TimelineMetronomeTempo(),
                _timeline.Project.TimeSignatureNumerator);
            PublishTimeline(false);
        }
    }

    private void TimelineSetTimeSignature(int numerator, int denominator)
    {
        if (_timeline == null || !_timeline.SetTimeSignature(numerator, denominator))
        {
            return;
        }

        _timelineMetronomeSource?.SetMetronome(
            TimelineMetronomeTempo(),
            _timeline.Project.TimeSignatureNumerator);
        PublishTimeline(false);
    }

    private void AddTimelineMarker(BridgeMessage message)
    {
        if (_timeline == null)
        {
            return;
        }

        long start = (long)Number(message, "startSample", _timeline.PlayheadSample);
        long? end = OptionalLong(message, "endSample");
        if (end.HasValue && end.Value <= start)
        {
            end = null;
        }

        _timeline.AddMarker(start, end, String(message, "name", ""));
        PublishTimeline(false);
    }

    private void UpdateTimelineMarker(BridgeMessage message)
    {
        if (_timeline == null)
        {
            return;
        }

        string id = String(message, "id");
        long? start = message.Payload.TryGetProperty("startSample", out var startValue) && startValue.TryGetDouble(out double startNumber)
            ? (long)startNumber
            : null;
        long? end = message.Payload.TryGetProperty("endSample", out var endValue) && endValue.TryGetDouble(out double endNumber)
            ? (long)endNumber
            : null;
        string? name = message.Payload.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == System.Text.Json.JsonValueKind.String
            ? nameValue.GetString()
            : null;
        if (_timeline.UpdateMarker(id, start, end, name))
        {
            PublishTimeline(false);
        }
    }

    private void RemoveTimelineMarker(string id)
    {
        if (_timeline?.RemoveMarker(id) == true)
        {
            PublishTimeline(false);
        }
    }

    private void PasteTimelineClips(BridgeMessage message)
    {
        if (_timeline == null ||
            !message.Payload.TryGetProperty("clips", out var clips) ||
            clips.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return;
        }

        var copies = new List<(string TrackId, string ClipId, long StartSample)>();
        foreach (var item in clips.EnumerateArray())
        {
            if (!item.TryGetProperty("trackId", out var trackValue) ||
                !item.TryGetProperty("clipId", out var clipValue) ||
                !item.TryGetProperty("startSample", out var startValue) ||
                trackValue.GetString() is not { } trackId ||
                clipValue.GetString() is not { } clipId ||
                !startValue.TryGetDouble(out double start))
            {
                continue;
            }

            copies.Add((trackId, clipId, (long)start));
        }

        if (_timeline.CopyClips(copies))
        {
            PublishTimeline(false);
        }
    }

    private async void NormalizeTimelineClip(string trackId, string clipId)
    {
        if (_timeline == null)
        {
            return;
        }

        TimelineService timeline = _timeline;
        bool ok;
        float gain;
        string error;
        try
        {
            (ok, gain, error) = await Task.Run(() =>
                timeline.NormalizeClip(trackId, clipId, out float value, out string message)
                    ? (true, value, "")
                    : (false, 1f, message));
        }
        catch (Exception ex)
        {
            ok = false;
            gain = 1f;
            error = ex.Message;
        }

        if (ok)
        {
            PublishTimeline(false);
            string message = $"Normalized clip to {20.0 * Math.Log10(Math.Max(0.0001f, gain)):0.0} dB gain.";
            SetStatus(message);
            ShowToast(message);
        }
        else
        {
            SetStatus(error, isError: true);
        }
    }

    private async void ReverseTimelineClip(string trackId, string clipId)
    {
        if (_timeline == null)
        {
            return;
        }

        string path = NextRecordingPath(AudioFileFormat.Wav24);
        SetStatus("Reversing timeline clip...");
        bool ok;
        string error;
        try
        {
            TimelineService timeline = _timeline;
            (ok, error) = await Task.Run(() => timeline.ReverseClip(trackId, clipId, path, out string message)
                ? (true, "")
                : (false, message));
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        if (ok)
        {
            RegisterGeneratedTimelineFile(path);
            PublishTimeline(false);
            SetStatus($"Reversed clip to {Path.GetFileName(path)}.");
            ShowToast("Clip reversed.");
        }
        else
        {
            SetStatus(error, isError: true);
        }
    }

    private async void ConsolidateTimeline(BridgeMessage message)
    {
        if (_timeline == null)
        {
            return;
        }

        var selections = new List<(string TrackId, string ClipId)>();
        if (message.Payload.TryGetProperty("clips", out var clips) &&
            clips.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in clips.EnumerateArray())
            {
                if (item.TryGetProperty("trackId", out var trackValue) &&
                    item.TryGetProperty("clipId", out var clipValue) &&
                    trackValue.GetString() is { } trackId &&
                    clipValue.GetString() is { } clipId)
                {
                    selections.Add((trackId, clipId));
                }
            }
        }

        long startSample = 0;
        long rangeStart = (long)Number(message, "startSample", -1);
        long rangeEnd = (long)Number(message, "endSample", -1);
        string path = NextRecordingPath(AudioFileFormat.Wav24);
        SetStatus("Consolidating timeline selection...");
        bool ok;
        float[] audio;
        string error;
        try
        {
            TimelineService timeline = _timeline;
            (ok, audio, startSample, _, error) = await Task.Run(() =>
            {
                if (selections.Count > 0)
                {
                    bool success = timeline.ConsolidateClips(
                        selections, path, out long start, out long length,
                        out float[] rendered, out string message);
                    return (success, rendered, start, length, success ? "" : message);
                }

                bool rangeSuccess = timeline.ConsolidateRange(
                    rangeStart, rangeEnd, path, out float[] rangeAudio, out string rangeError);
                return (rangeSuccess, rangeAudio, rangeStart, rangeEnd - rangeStart,
                    rangeSuccess ? "" : rangeError);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            audio = Array.Empty<float>();
            error = ex.Message;
        }

        if (!ok)
        {
            SetStatus(error, isError: true);
            return;
        }

        bool clipAdded = false;
        try
        {
            var track = AddTimelineTrack("Consolidated");
            _timeline.SetTrackMuted(track.Id, true);
            if (!_timeline.AddClipFromAudio(track.Id, path, audio, out string importError,
                    Math.Max(0, startSample)))
            {
                RemoveTimelineTrack(track.Id);
                TryDeleteTimelineFile(path);
                SetStatus(importError, isError: true);
                return;
            }

            clipAdded = true;
            RegisterGeneratedTimelineFile(path);
            PublishTimeline(true);
            PublishGraph();
            SetStatus("Consolidated clip created on a muted track. Unmute it to use the render.");
            ShowToast("Consolidated clip created on a muted track.");
        }
        catch (Exception ex)
        {
            if (!clipAdded)
            {
                TryDeleteTimelineFile(path);
            }

            SetStatus("Could not add the consolidated clip: " + ex.Message, isError: true);
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
