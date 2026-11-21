using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Sample-addressed timeline playback for the existing graph. The render thread
/// calls <see cref="RenderBlock"/> once per graph block; track sources publish
/// exactly the frames for that block, so clip starts and seeks are sample
/// accurate relative to the engine clock.
/// </summary>
public sealed partial class TimelineService : IDisposable
{
    public const string ClipPitchAutomationPrefix = "clip:";
    public const float MaxClipPitchSemitones = 24f;

    private const int MaxClipSamples = 48_000 * 60 * 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, TrackPlayback> _tracks = new();
    private List<TimelineClip> _clipboard = new();
    private TrackPlayback[] _trackSnapshot = Array.Empty<TrackPlayback>();
    private TimelineProject _project = new();
    private long _playheadSample;
    private int _seekGeneration;
    private volatile bool _playing;
    private volatile RenderState _renderState = new(Array.Empty<TrackPlayback>(), -1, -1);

    private sealed record HistorySnapshot(TimelineProject Project, long MetadataBytes);
    private sealed record RenderState(TrackPlayback[] Tracks, long LoopStart, long LoopEnd);

    private readonly List<HistorySnapshot> _undoHistory = new();
    private readonly List<HistorySnapshot> _redoHistory = new();
    private readonly Dictionary<float[], int> _historyArrays = new(ReferenceEqualityComparer.Instance);
    private long _historyArrayBytes;
    private long _historyMetadataBytes;
    private int _maxUndoSteps = 50;
    private long _maxUndoBytes = 512L * 1024 * 1024;

    private void PublishRenderState() =>
        _renderState = new(_trackSnapshot, _project.LoopStartSample, _project.LoopEndSample);

    public TimelineProject Project => _project;
    public int SampleRate => AudioEngineConfig.SampleRate;
    public long PlayheadSample { get { lock (_gate) return _playheadSample; } }
    public bool IsPlaying { get { lock (_gate) return _playing; } }
    public bool HasTracks { get { lock (_gate) return _tracks.Count > 0; } }
    public bool CanUndo { get { lock (_gate) return _undoHistory.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redoHistory.Count > 0; } }
    public long HistoryBytes { get { lock (_gate) return _historyMetadataBytes + _historyArrayBytes; } }

    public void SetHistoryLimits(int maxSteps, long maxBytes)
    {
        lock (_gate)
        {
            _maxUndoSteps = Math.Max(1, maxSteps);
            _maxUndoBytes = Math.Max(16L * 1024 * 1024, maxBytes);
            TrimHistory();
        }
    }
    public Action<string, int, int, float>? ApplyAutomation { get; set; }
    public Action<string, float>? ApplyGeneratorGate { get; set; }

    /// <summary>
    /// Optional offline processors used by BounceToWave. The renderer receives
    /// one track at a time, then the summed master block. Instances must be
    /// stateful for the whole bounce so reverb and delay tails are continuous.
    /// </summary>
    public Action<TimelineTrack, float[], int>? TrackProcessor { get; set; }
    public Action<float[], int>? MasterProcessor { get; set; }
    public double BounceTailSeconds { get; set; } = 2.0;

    public void LoadProject(TimelineProject? project)
    {
        project ??= new TimelineProject();
        project.Tracks ??= new();
        project.Tracks.RemoveAll(track => track == null);
        foreach (var track in project.Tracks)
        {
            track.Id = string.IsNullOrWhiteSpace(track.Id) ? Guid.NewGuid().ToString("N") : track.Id;
            track.Clips ??= new();
            track.Clips.RemoveAll(clip => clip == null);
            track.Automation ??= new();
            track.Automation.RemoveAll(lane => lane == null);
            foreach (var clip in track.Clips)
            {
                clip.Id = string.IsNullOrWhiteSpace(clip.Id) ? Guid.NewGuid().ToString("N") : clip.Id;
                clip.Peaks ??= Array.Empty<float>();
            }

            foreach (var lane in track.Automation)
            {
                lane.Id = string.IsNullOrWhiteSpace(lane.Id) ? Guid.NewGuid().ToString("N") : lane.Id;
                lane.Points ??= new();
                lane.Points.RemoveAll(point => point == null);
            }
        }

        if (!double.IsFinite(project.Tempo) || project.Tempo < 20.0 || project.Tempo > 300.0)
        {
            project.Tempo = 120.0;
        }

        project.TimeSignatureNumerator = Math.Clamp(project.TimeSignatureNumerator, 1, 16);
        project.TimeSignatureDenominator = project.TimeSignatureDenominator is 1 or 2 or 4 or 8 or 16
            ? project.TimeSignatureDenominator
            : 4;
        project.Markers ??= new();
        project.Markers.RemoveAll(marker => marker == null);
        foreach (var marker in project.Markers)
        {
            marker.Id = string.IsNullOrWhiteSpace(marker.Id) ? Guid.NewGuid().ToString("N") : marker.Id;
            marker.StartSample = Math.Max(0, marker.StartSample);
            marker.EndSample = marker.EndSample > marker.StartSample ? marker.EndSample : -1;
        }

        if (project.LoopStartSample < 0 || project.LoopEndSample <= project.LoopStartSample)
        {
            project.LoopStartSample = -1;
            project.LoopEndSample = -1;
        }

        foreach (var track in project.Tracks)
        {
            foreach (var lane in track.Automation)
            {
                lane.Points = NormalizeAutomationPoints(lane.Points);
            }
        }

        var loaded = new List<(TimelineTrack Track, float[]?[] Audio)>();
        foreach (var track in project.Tracks)
        {
            var audio = new float[]?[track.Clips.Count];
            for (int i = 0; i < track.Clips.Count; i++)
            {
                var clip = track.Clips[i];
                try
                {
                    audio[i] = TrustedPath.IsLocalFile(clip.SourcePath) && File.Exists(clip.SourcePath)
                        ? LoadClip(clip.SourcePath)
                        : null;
                }
                catch (Exception ex)
                {
                    Ruse.App.Diagnostics.AppLog.Warning($"Timeline clip '{clip.Name}' could not be restored: {ex.Message}");
                    audio[i] = null;
                }

                if (clip.Peaks.Length == 0 && audio[i] != null)
                {
                    clip.Peaks = ComputePeaks(audio[i]!);
                }
            }

            loaded.Add((track, audio));
        }

        lock (_gate)
        {
            _project = project;
            _tracks.Clear();
            foreach (var (track, audio) in loaded)
            {
                var playback = new TrackPlayback(track);
                for (int i = 0; i < track.Clips.Count; i++)
                {
                    track.Clips[i].Audio = audio[i];
                    if (audio[i] != null && track.Clips[i].LengthSamples <= 0)
                    {
                        track.Clips[i].LengthSamples = audio[i]!.Length / 2;
                    }
                }

                _tracks[track.Id] = playback;
            }

            _trackSnapshot = _tracks.Values.ToArray();
            _playheadSample = 0;
            _playing = false;
            PublishRenderState();
            ClearHistory();
        }
    }

    public TimelineTrack AddTrack(string? name = null)
    {
        lock (_gate)
        {
            var track = new TimelineTrack
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Track {_tracks.Count + 1}" : name!,
            };
            _project.Tracks.Add(track);
            _tracks[track.Id] = new TrackPlayback(track);
            _trackSnapshot = _tracks.Values.ToArray();
            PublishRenderState();
            ClearRedo();
            return track;
        }
    }

    public TimelineTrack AddGeneratorTrack(string generatorNodeId, string? name = null)
    {
        lock (_gate)
        {
            var track = new TimelineTrack
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Track {_tracks.Count + 1}" : name!,
                GeneratorNodeId = generatorNodeId,
            };
            _project.Tracks.Add(track);
            _tracks[track.Id] = new TrackPlayback(track);
            _trackSnapshot = _tracks.Values.ToArray();
            PublishRenderState();
            ClearRedo();
            return track;
        }
    }

    public void AttachSource(string trackId, SourceNode source)
    {
        lock (_gate)
        {
            if (_tracks.TryGetValue(trackId, out var playback))
            {
                playback.Source = source;
                playback.Track.SourceNodeId = source.Id;
            }
        }
    }

    public bool AddGeneratorClip(string trackId, long startSample, long lengthSamples, string? name, out string error)
    {
        error = "";
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            if (lengthSamples <= 0)
            {
                error = "The generator clip needs a duration.";
                return false;
            }

            Remember();
            playback.Track.Clips.Add(new TimelineClip
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Generator" : name,
                StartSample = Math.Max(0, startSample),
                LengthSamples = lengthSamples,
            });
            playback.RefreshClips();
            playback.RefreshAutomation();
            return true;
        }
    }

    public bool AddClip(string trackId, string path, out string error, long? startSample = null)
    {
        error = "";
        float[] audio;
        try
        {
            audio = LoadClip(path);
        }
        catch (Exception ex)
        {
            error = "Could not load the clip: " + ex.Message;
            return false;
        }

        return AddClipFromAudio(trackId, path, audio, out error, startSample);
    }

    /// <summary>
    /// Adds a clip whose audio is already decoded. Offline renders use this to
    /// avoid reading the file they just wrote back from disk.
    /// </summary>
    public bool AddClipFromAudio(
        string trackId, string path, float[] audio, out string error, long? startSample = null)
    {
        error = "";
        if (audio.Length == 0)
        {
            error = "The clip contains no audio.";
            return false;
        }

        if (audio.Length / 2 > MaxClipSamples)
        {
            error = "The clip is longer than the 10 minute MVP limit.";
            return false;
        }

        float[] peaks = ComputePeaks(audio);

        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            Remember();
            long start = startSample.HasValue ? Math.Max(0, startSample.Value) : 0;
            if (!startSample.HasValue)
            {
                foreach (var existing in playback.Track.Clips)
                {
                    start = Math.Max(start, existing.StartSample + existing.LengthSamples);
                }
            }

            var clip = new TimelineClip
            {
                Name = Path.GetFileNameWithoutExtension(path),
                SourcePath = path,
                StartSample = start,
                LengthSamples = audio.Length / 2,
                Audio = audio,
                Peaks = peaks,
            };
            playback.Track.Clips.Add(clip);
            playback.RefreshClips();
            playback.RefreshAutomation();
        }

        return true;
    }

    public bool UpdateTrack(string trackId, float gain, bool muted, bool solo)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            float nextGain = Math.Clamp(gain, 0f, 2f);
            if (playback.Track.Gain == nextGain && playback.Track.Muted == muted && playback.Track.Solo == solo)
            {
                return true;
            }

            Remember();
            playback.Track.Gain = nextGain;
            playback.Track.Muted = muted;
            playback.Track.Solo = solo;
            return true;
        }
    }

    public bool RenameTrack(string trackId, string name, out string error)
    {
        error = "";
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            error = "Track name cannot be empty.";
            return false;
        }

        if (name.Length > 80)
        {
            name = name[..80];
        }

        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            if (playback.Track.Name == name)
            {
                return true;
            }

            Remember();
            playback.Track.Name = name;
            return true;
        }
    }

    public bool StretchClip(string trackId, string clipId, long lengthSamples, string outputPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "No stretch destination was provided.";
            return false;
        }

        float[] sourceAudio;
        int sourceFrames;
        int sourceStart;
        int segmentFrames;
        int fadeIn;
        int fadeOut;
        long originalOffset;
        long originalLength;
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip?.Audio == null || clip.LengthSamples <= 0)
            {
                error = "The clip has no source audio.";
                return false;
            }

            sourceAudio = clip.Audio;
            sourceFrames = sourceAudio.Length / 2;
            sourceStart = (int)Math.Clamp(clip.SourceOffsetSamples, 0, Math.Max(0, sourceFrames - 1));
            segmentFrames = (int)Math.Min(clip.LengthSamples, sourceFrames - sourceStart);
            fadeIn = clip.FadeInSamples;
            fadeOut = clip.FadeOutSamples;
            originalOffset = clip.SourceOffsetSamples;
            originalLength = clip.LengthSamples;
        }

        if (segmentFrames <= 0)
        {
            error = "The clip has no source audio.";
            return false;
        }

        double factor = lengthSamples / (double)segmentFrames;
        if (factor < 0.5 || factor > 2.0)
        {
            error = "Timeline stretch is limited to 0.5x to 2x.";
            return false;
        }

        if (lengthSamples == segmentFrames)
        {
            return true;
        }

        try
        {
            int target = (int)lengthSamples;
            var pitch = new PitchShiftEffect
            {
                FftSize = 2048,
                Oversampling = 4,
                Mix = 1f,
                PitchSemitones = (float)(12.0 * Math.Log2(factor)),
            };
            pitch.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
            int latency = Math.Max(0, pitch.LatencySamples);
            var resampled = new float[(target + latency) * 2];
            for (int frame = 0; frame < target; frame++)
            {
                double sourcePosition = frame / factor;
                int index = Math.Min(segmentFrames - 1, (int)sourcePosition);
                int next = Math.Min(segmentFrames - 1, index + 1);
                float t = (float)(sourcePosition - index);
                int from = (sourceStart + index) * 2;
                int to = (sourceStart + next) * 2;
                resampled[frame * 2] = sourceAudio[from] * (1f - t) + sourceAudio[to] * t;
                resampled[(frame * 2) + 1] = sourceAudio[from + 1] * (1f - t) + sourceAudio[to + 1] * t;
            }

            var processed = new float[target * 2];
            pitch.Process(resampled.AsSpan());
            Array.Copy(resampled, latency * 2, processed, 0,
                Math.Min(processed.Length, Math.Max(0, resampled.Length - latency * 2)));
            for (int i = 0; i < processed.Length; i++)
            {
                if (!float.IsFinite(processed[i]))
                {
                    processed[i] = 0f;
                }
            }

            using (var writer = new WaveFileWriter(outputPath, new WaveFormat(SampleRate, 24, 2)))
            {
                writer.WriteSamples(processed, 0, processed.Length);
            }

            lock (_gate)
            {
                if (!_tracks.TryGetValue(trackId, out var playback))
                {
                    DeletePath(outputPath);
                    error = "Timeline track not found.";
                    return false;
                }

                var clip = playback.Track.Clips.FirstOrDefault(item =>
                    string.Equals(item.Id, clipId, StringComparison.Ordinal));
                if (clip == null || !ReferenceEquals(clip.Audio, sourceAudio) ||
                    clip.SourceOffsetSamples != originalOffset || clip.LengthSamples != originalLength)
                {
                    DeletePath(outputPath);
                    error = "The clip changed while stretching.";
                    return false;
                }

                Remember();
                clip.Audio = processed;
                clip.SourcePath = outputPath;
                clip.SourceOffsetSamples = 0;
                clip.LengthSamples = target;
                clip.FadeInSamples = (int)Math.Min(target, Math.Round(fadeIn * factor));
                clip.FadeOutSamples = (int)Math.Min(target, Math.Round(fadeOut * factor));
                clip.Peaks = ComputePeaks(processed);
            }

            return true;
        }
        catch (Exception ex)
        {
            DeletePath(outputPath);
            error = "Could not stretch the clip: " + ex.Message;
            return false;
        }
    }

    public bool NormalizeClip(string trackId, string clipId, out float gain, out string error)
    {
        gain = 1f;
        error = "";
        float[] audio;
        long sourceOffset;
        long lengthSamples;
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip?.Audio == null || clip.LengthSamples <= 0)
            {
                error = "The clip has no source audio.";
                return false;
            }

            audio = clip.Audio;
            sourceOffset = clip.SourceOffsetSamples;
            lengthSamples = clip.LengthSamples;
        }

        int audioFrames = audio.Length / 2;
        int start = (int)Math.Clamp(sourceOffset, 0, Math.Max(0, audioFrames - 1));
        int frames = (int)Math.Min(lengthSamples, audioFrames - start);
        float peak = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = (start + frame) * 2;
            peak = Math.Max(peak, Math.Max(Math.Abs(audio[index]), Math.Abs(audio[index + 1])));
        }

        if (peak < 1e-6f)
        {
            error = "The clip is silent.";
            return false;
        }

        float nextGain = Math.Clamp(0.987f / peak, 0.0001f, 4f);
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null ||
                !ReferenceEquals(clip.Audio, audio) ||
                clip.SourceOffsetSamples != sourceOffset ||
                clip.LengthSamples != lengthSamples)
            {
                error = "The clip changed while normalizing.";
                return false;
            }

            Remember();
            clip.Gain = nextGain;
            gain = nextGain;
            return true;
        }
    }

    public bool ReverseClip(string trackId, string clipId, string outputPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "No reverse destination was provided.";
            return false;
        }

        float[] sourceAudio;
        int sourceStart;
        int sourceFrames;
        int originalLength;
        bool loop;
        int fadeIn;
        int fadeOut;
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                error = "Timeline track not found.";
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip?.Audio == null || clip.LengthSamples <= 0)
            {
                error = "The clip has no source audio.";
                return false;
            }

            sourceAudio = clip.Audio;
            sourceStart = (int)Math.Clamp(clip.SourceOffsetSamples, 0, Math.Max(0, (sourceAudio.Length / 2) - 1));
            sourceFrames = Math.Min((int)Math.Min(int.MaxValue, clip.LengthSamples), (sourceAudio.Length / 2) - sourceStart);
            originalLength = (int)Math.Min(int.MaxValue, clip.LengthSamples);
            loop = clip.Loop;
            fadeIn = clip.FadeInSamples;
            fadeOut = clip.FadeOutSamples;
        }

        if (sourceFrames <= 0)
        {
            error = "The clip has no source audio.";
            return false;
        }

        var reversed = new float[sourceFrames * 2];
        for (int frame = 0; frame < sourceFrames; frame++)
        {
            int from = (sourceStart + sourceFrames - 1 - frame) * 2;
            reversed[frame * 2] = sourceAudio[from];
            reversed[frame * 2 + 1] = sourceAudio[from + 1];
        }

        try
        {
            using (var writer = new WaveFileWriter(outputPath, new WaveFormat(SampleRate, 24, 2)))
            {
                writer.WriteSamples(reversed, 0, reversed.Length);
            }

            lock (_gate)
            {
                if (!_tracks.TryGetValue(trackId, out var playback))
                {
                    DeletePath(outputPath);
                    error = "Timeline track not found.";
                    return false;
                }

                var clip = playback.Track.Clips.FirstOrDefault(item =>
                    string.Equals(item.Id, clipId, StringComparison.Ordinal));
                if (clip == null || !ReferenceEquals(clip.Audio, sourceAudio))
                {
                    DeletePath(outputPath);
                    error = "The clip changed while reversing.";
                    return false;
                }

                Remember();
                clip.Audio = reversed;
                clip.SourcePath = outputPath;
                clip.SourceOffsetSamples = 0;
                clip.LengthSamples = loop ? originalLength : sourceFrames;
                clip.FadeInSamples = Math.Min((int)clip.LengthSamples, fadeOut);
                clip.FadeOutSamples = Math.Min((int)clip.LengthSamples, fadeIn);
                clip.Peaks = ComputePeaks(reversed);
            }

            return true;
        }
        catch (Exception ex)
        {
            DeletePath(outputPath);
            error = "Could not reverse the clip: " + ex.Message;
            return false;
        }
    }

    public bool ConsolidateClips(
        IReadOnlyList<(string TrackId, string ClipId)> selections,
        string outputPath,
        out long startSample,
        out long lengthSamples,
        out float[] audio,
        out string error)
    {
        startSample = 0;
        lengthSamples = 0;
        audio = Array.Empty<float>();
        error = "";
        if (selections.Count == 0)
        {
            error = "Select at least one clip to consolidate.";
            return false;
        }

        var selected = new List<(TimelineClip Clip, float Gain)>();
        lock (_gate)
        {
            var snapshot = CloneProject(_project);
            var tracks = snapshot.Tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
            foreach (var selection in selections)
            {
                if (!tracks.TryGetValue(selection.TrackId, out var track))
                {
                    continue;
                }

                var clip = track.Clips.FirstOrDefault(item =>
                    string.Equals(item.Id, selection.ClipId, StringComparison.Ordinal));
                if (clip?.Audio != null)
                {
                    selected.Add((clip, track.Gain));
                }
            }

            if (selected.Count == 0)
            {
                error = "The selected clips have no audio to consolidate.";
                return false;
            }

            startSample = selected.Min(item => item.Clip.StartSample);
            lengthSamples = selected.Max(item => item.Clip.StartSample + item.Clip.LengthSamples) - startSample;
        }

        if (lengthSamples > MaxClipSamples)
        {
            error = "The selected clips are longer than the 10 minute clip limit.";
            return false;
        }

        return RenderConsolidated(selected, startSample, lengthSamples, outputPath, out audio, out error);
    }

    public bool ConsolidateRange(
        long startSample, long endSample, string outputPath, out float[] audio, out string error)
    {
        audio = Array.Empty<float>();
        error = "";
        if (startSample < 0 || endSample <= startSample)
        {
            error = "Select a valid time range to consolidate.";
            return false;
        }

        List<(TimelineTrack Track, bool Audible)> tracks;
        bool anySolo;
        lock (_gate)
        {
            var snapshot = CloneProject(_project);
            long projectEnd = 0;
            foreach (var track in snapshot.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    projectEnd = Math.Max(projectEnd, clip.StartSample + clip.LengthSamples);
                }
            }

            if (startSample >= projectEnd)
            {
                error = "The selected range is outside the timeline.";
                return false;
            }

            endSample = Math.Min(endSample, projectEnd);
            if (endSample <= startSample)
            {
                error = "The selected range has no audio.";
                return false;
            }

            if (endSample - startSample > MaxClipSamples)
            {
                error = "The selected range is longer than the 10 minute clip limit.";
                return false;
            }

            anySolo = snapshot.Tracks.Any(track => track.Solo);
            tracks = snapshot.Tracks
                .Select(track => (track, !track.Muted && (!anySolo || track.Solo)))
                .ToList();
        }

        try
        {
            const int blockFrames = 2048;
            var block = new float[blockFrames * 2];
            var trackBlock = new float[blockFrames * 2];
            var playback = new Dictionary<string, ClipPlayback>(StringComparer.Ordinal);
            var rendered = new float[(int)(endSample - startSample) * 2];
            int writeOffset = 0;
            using var writer = new WaveFileWriter(outputPath, new WaveFormat(SampleRate, 24, 2));
            long position = startSample;
            while (position < endSample)
            {
                int frames = (int)Math.Min(blockFrames, endSample - position);
                Array.Clear(block, 0, frames * 2);
                foreach (var (track, audible) in tracks)
                {
                    if (!audible)
                    {
                        continue;
                    }

                    Array.Clear(trackBlock, 0, frames * 2);
                    MixTrack(track, playback, position, frames, trackBlock);
                    for (int i = 0; i < frames * 2; i++)
                    {
                        block[i] += trackBlock[i];
                    }
                }

                Array.Copy(block, 0, rendered, writeOffset, frames * 2);
                writeOffset += frames * 2;
                writer.WriteSamples(block, 0, frames * 2);
                position += frames;
            }

            audio = rendered;
            return true;
        }
        catch (Exception ex)
        {
            DeletePath(outputPath);
            audio = Array.Empty<float>();
            error = "Could not consolidate the range: " + ex.Message;
            return false;
        }
    }

    private static bool RenderConsolidated(
        IReadOnlyList<(TimelineClip Clip, float Gain)> clips,
        long startSample,
        long lengthSamples,
        string outputPath,
        out float[] audio,
        out string error)
    {
        audio = Array.Empty<float>();
        error = "";
        if (lengthSamples <= 0 || lengthSamples > MaxClipSamples)
        {
            error = "The consolidated selection is longer than the 10 minute clip limit.";
            return false;
        }

        try
        {
            const int blockFrames = 2048;
            var block = new float[blockFrames * 2];
            var playback = new Dictionary<string, ClipPlayback>(StringComparer.Ordinal);
            var rendered = new float[(int)lengthSamples * 2];
            int writeOffset = 0;
            using var writer = new WaveFileWriter(outputPath,
                new WaveFormat(AudioEngineConfig.SampleRate, 24, 2));
            long position = startSample;
            long end = startSample + lengthSamples;
            while (position < end)
            {
                int frames = (int)Math.Min(blockFrames, end - position);
                Array.Clear(block, 0, frames * 2);
                foreach (var (clip, gain) in clips)
                {
                    if (!playback.TryGetValue(clip.Id, out var state))
                    {
                        state = new ClipPlayback();
                        playback[clip.Id] = state;
                    }

                    MixClipInto(clip, state, gain, position, frames, block, 0);
                }

                Array.Copy(block, 0, rendered, writeOffset, frames * 2);
                writeOffset += frames * 2;
                writer.WriteSamples(block, 0, frames * 2);
                position += frames;
            }

            audio = rendered;
            return true;
        }
        catch (Exception ex)
        {
            DeletePath(outputPath);
            audio = Array.Empty<float>();
            error = "Could not consolidate the clips: " + ex.Message;
            return false;
        }
    }
    private static void DeletePath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
    public bool SetTrackGain(string trackId, float gain)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            float nextGain = Math.Clamp(gain, 0f, 2f);
            if (playback.Track.Gain == nextGain)
            {
                return true;
            }

            Remember();
            playback.Track.Gain = nextGain;
            return true;
        }
    }

    public bool SetTrackSolo(string trackId, bool solo)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            if (playback.Track.Solo == solo)
            {
                return true;
            }

            Remember();
            playback.Track.Solo = solo;
            return true;
        }
    }

    public bool SetTrackMuted(string trackId, bool muted)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            if (playback.Track.Muted == muted)
            {
                return true;
            }

            Remember();
            playback.Track.Muted = muted;
            return true;
        }
    }

    public bool UpdateClip(string trackId, string clipId,
        long? startSample, long? lengthSamples, long? sourceOffsetSamples,
        float? gain, int? fadeInSamples, int? fadeOutSamples, float? pitchSemitones = null)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null)
            {
                return false;
            }

            Remember();
            if (startSample.HasValue) clip.StartSample = Math.Max(0, startSample.Value);
            if (sourceOffsetSamples.HasValue)
            {
                long maxOffset = clip.Audio == null ? long.MaxValue : Math.Max(0, (clip.Audio.Length / 2) - 1);
                clip.SourceOffsetSamples = Math.Clamp(sourceOffsetSamples.Value, 0, maxOffset);
            }

            if (lengthSamples.HasValue)
            {
                int available = clip.Audio == null ? int.MaxValue : Math.Max(1, (clip.Audio.Length / 2) - (int)clip.SourceOffsetSamples);
                clip.LengthSamples = clip.Loop
                    ? Math.Max(1, lengthSamples.Value)
                    : Math.Clamp(lengthSamples.Value, 1, available);
            }

            if (gain.HasValue) clip.Gain = Math.Clamp(gain.Value, 0f, 4f);
            if (pitchSemitones.HasValue)
            {
                clip.PitchSemitones = Math.Clamp(pitchSemitones.Value,
                    -MaxClipPitchSemitones, MaxClipPitchSemitones);
            }

            if (fadeInSamples.HasValue) clip.FadeInSamples = Math.Max(0, fadeInSamples.Value);
            if (fadeOutSamples.HasValue) clip.FadeOutSamples = Math.Max(0, fadeOutSamples.Value);
            clip.FadeInSamples = (int)Math.Min(clip.FadeInSamples, clip.LengthSamples);
            clip.FadeOutSamples = (int)Math.Min(clip.FadeOutSamples, clip.LengthSamples);
            return true;
        }
    }

    public TimelineAutomationLane AddAutomationLane(string trackId, string nodeId, int processorIndex, int parameterIndex, float value)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                throw new InvalidOperationException("Timeline track not found.");
            }

            var existing = playback.Track.Automation.FirstOrDefault(item =>
                string.Equals(item.TargetNodeId, nodeId, StringComparison.Ordinal) &&
                item.ProcessorIndex == processorIndex &&
                item.ParameterIndex == parameterIndex);
            if (existing != null)
            {
                return existing;
            }

            Remember();
            var lane = new TimelineAutomationLane
            {
                TargetNodeId = nodeId,
                ProcessorIndex = processorIndex,
                ParameterIndex = parameterIndex,
                Points =
                {
                    new TimelineAutomationPoint { Sample = 0, Value = value },
                },
            };
            playback.Track.Automation.Add(lane);
            playback.RefreshAutomation();
            return lane;
        }
    }

    public bool SetAutomationPoints(string trackId, string laneId, IEnumerable<TimelineAutomationPoint> points)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var lane = playback.Track.Automation.FirstOrDefault(item =>
                string.Equals(item.Id, laneId, StringComparison.Ordinal));
            if (lane == null)
            {
                return false;
            }

            Remember();
            lane.Points = NormalizeAutomationPoints(points);
            return true;
        }
    }

    public bool RemoveAutomationLane(string trackId, string laneId)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback) ||
                !playback.Track.Automation.Any(lane => string.Equals(lane.Id, laneId, StringComparison.Ordinal)))
            {
                return false;
            }

            Remember();
            playback.Track.Automation.RemoveAll(lane =>
                string.Equals(lane.Id, laneId, StringComparison.Ordinal));
            playback.RefreshAutomation();
            return true;
        }
    }

    public bool RemoveTrack(string trackId)
    {
        lock (_gate)
        {
            if (!_tracks.ContainsKey(trackId))
            {
                return false;
            }

            Remember();
            _tracks.Remove(trackId);
            _project.Tracks.RemoveAll(track => string.Equals(track.Id, trackId, StringComparison.Ordinal));
            _trackSnapshot = _tracks.Values.ToArray();
            PublishRenderState();
            return true;
        }
    }

    public bool SplitClip(string trackId, string clipId, long sample)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null)
            {
                return false;
            }

            long leftLength = sample - clip.StartSample;
            if (leftLength <= 0 || leftLength >= clip.LengthSamples)
            {
                return false;
            }

            Remember();
            long rightLength = clip.LengthSamples - leftLength;
            var right = new TimelineClip
            {
                Name = clip.Name,
                SourcePath = clip.SourcePath,
                StartSample = sample,
                LengthSamples = rightLength,
                SourceOffsetSamples = clip.SourceOffsetSamples + leftLength,
                Gain = clip.Gain,
                PitchSemitones = clip.PitchSemitones,
                FadeInSamples = 0,
                FadeOutSamples = (int)Math.Min(clip.FadeOutSamples, rightLength),
                Peaks = clip.Peaks,
                Audio = clip.Audio,
            };

            clip.LengthSamples = leftLength;
            clip.FadeInSamples = (int)Math.Min(clip.FadeInSamples, leftLength);
            clip.FadeOutSamples = 0;
            playback.Track.Clips.Add(right);
            playback.RefreshClips();
            playback.RefreshAutomation();
            return true;
        }
    }

    public bool DuplicateClip(string trackId, string clipId)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null)
            {
                return false;
            }

            Remember();
            playback.Track.Clips.Add(new TimelineClip
            {
                Name = clip.Name,
                SourcePath = clip.SourcePath,
                StartSample = clip.StartSample + clip.LengthSamples,
                LengthSamples = clip.LengthSamples,
                SourceOffsetSamples = clip.SourceOffsetSamples,
                Gain = clip.Gain,
                PitchSemitones = clip.PitchSemitones,
                FadeInSamples = clip.FadeInSamples,
                FadeOutSamples = clip.FadeOutSamples,
                Peaks = clip.Peaks,
                Audio = clip.Audio,
            });
            playback.RefreshClips();
            playback.RefreshAutomation();
            return true;
        }
    }

    public bool CrossfadeClip(string trackId, string clipId)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null)
            {
                return false;
            }

            var other = playback.Track.Clips
                .Where(item => !string.Equals(item.Id, clip.Id, StringComparison.Ordinal))
                .Where(item => item.StartSample < clip.StartSample + clip.LengthSamples &&
                               item.StartSample + item.LengthSamples > clip.StartSample)
                .OrderBy(item => Math.Abs(item.StartSample - clip.StartSample))
                .FirstOrDefault();
            if (other == null)
            {
                return false;
            }

            long overlapStart = Math.Max(clip.StartSample, other.StartSample);
            long overlapEnd = Math.Min(clip.StartSample + clip.LengthSamples,
                other.StartSample + other.LengthSamples);
            long overlap = overlapEnd - overlapStart;
            if (overlap <= 0)
            {
                return false;
            }

            Remember();
            var left = clip.StartSample <= other.StartSample ? clip : other;
            var right = ReferenceEquals(left, clip) ? other : clip;
            left.FadeOutSamples = (int)Math.Min(overlap, left.LengthSamples);
            right.FadeInSamples = (int)Math.Min(overlap, right.LengthSamples);
            playback.RefreshClips();
            playback.RefreshAutomation();
            return true;
        }
    }

    public bool SetClipLoop(string trackId, string clipId, bool loop)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null) return false;
            Remember();
            clip.Loop = loop;
            return true;
        }
    }

    public bool RemoveClip(string trackId, string clipId)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback) ||
                !playback.Track.Clips.Any(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal)))
            {
                return false;
            }

            Remember();
            playback.Track.Clips.RemoveAll(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
            playback.RefreshClips();
            playback.RefreshAutomation();
            return true;
        }
    }


    public bool MoveClip(string trackId, string clipId, long startSample)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var playback))
            {
                return false;
            }

            var clip = playback.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null)
            {
                return false;
            }

            long nextStart = Math.Max(0, startSample);
            if (clip.StartSample == nextStart)
            {
                return true;
            }

            Remember();
            clip.StartSample = nextStart;
            return true;
        }
    }

    /// <summary>Moves a clip to another timeline track without changing its audio.</summary>
    public bool MoveClipToTrack(string fromTrackId, string toTrackId, string clipId, long startSample)
    {
        lock (_gate)
        {
            if (string.Equals(fromTrackId, toTrackId, StringComparison.Ordinal))
            {
                return MoveClip(fromTrackId, clipId, startSample);
            }

            if (!_tracks.TryGetValue(fromTrackId, out var from) ||
                !_tracks.TryGetValue(toTrackId, out var to))
            {
                return false;
            }

            var clip = from.Track.Clips.FirstOrDefault(item =>
                string.Equals(item.Id, clipId, StringComparison.Ordinal));
            if (clip == null)
            {
                return false;
            }

            bool fromGenerator = !string.IsNullOrWhiteSpace(from.Track.GeneratorNodeId);
            bool toGenerator = !string.IsNullOrWhiteSpace(to.Track.GeneratorNodeId);
            if (fromGenerator != toGenerator ||
                (fromGenerator && !string.Equals(from.Track.GeneratorNodeId,
                    to.Track.GeneratorNodeId, StringComparison.Ordinal)))
            {
                return false;
            }

            Remember();
            from.Track.Clips.Remove(clip);
            clip.StartSample = Math.Max(0, startSample);
            to.Track.Clips.Add(clip);
            return true;
        }
    }

    public int RenameSourcePath(string oldPath, string newPath)
    {
        lock (_gate)
        {
            int changed = 0;
            foreach (var track in _project.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (string.Equals(clip.SourcePath, oldPath, StringComparison.OrdinalIgnoreCase))
                    {
                        clip.SourcePath = newPath;
                        changed++;
                    }
                }
            }

            return changed;
        }
    }

    public long TotalLengthSamples()
    {
        lock (_gate)
        {
            long end = 0;
            foreach (var track in _project.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    end = Math.Max(end, clip.StartSample + clip.LengthSamples);
                }
            }

            return end;
        }
    }

    /// <summary>
    /// Full paths referenced by the live project and both undo stacks. Offline
    /// renders use this to keep files that a history step can still restore.
    /// </summary>
    public HashSet<string> ReferencedSourcePaths(bool includeHistory = true)
    {
        lock (_gate)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddReferencedSourcePaths(_project, paths);
            if (!includeHistory)
            {
                return paths;
            }

            foreach (var snapshot in _undoHistory)
            {
                AddReferencedSourcePaths(snapshot.Project, paths);
            }

            foreach (var snapshot in _redoHistory)
            {
                AddReferencedSourcePaths(snapshot.Project, paths);
            }

            return paths;
        }
    }

    private static void AddReferencedSourcePaths(TimelineProject project, HashSet<string> paths)
    {
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (string.IsNullOrWhiteSpace(clip.SourcePath))
                {
                    continue;
                }

                try
                {
                    paths.Add(Path.GetFullPath(clip.SourcePath));
                }
                catch
                {
                    paths.Add(clip.SourcePath);
                }
            }
        }
    }

    public bool BounceToWave(string path, out string error)
    {
        error = "";
        TimelineProject snapshot;
        long length;
        lock (_gate)
        {
            snapshot = CloneProject(_project);
            length = 0;
            foreach (var track in snapshot.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    length = Math.Max(length, clip.StartSample + clip.LengthSamples);
                }
            }
        }

        if (length <= 0)
        {
            error = "The timeline has no audio to bounce.";
            return false;
        }

        if (TrackProcessor != null || MasterProcessor != null)
        {
            length += (long)Math.Round(Math.Max(0.0, BounceTailSeconds) * SampleRate);
        }

        try
        {
            const int blockFrames = 2048;
            var block = new float[blockFrames * 2];
            var trackBlock = new float[blockFrames * 2];
            var clipPlayback = new Dictionary<string, ClipPlayback>(StringComparer.Ordinal);
            bool anySolo = snapshot.Tracks.Any(track => track.Solo);
            using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 24, 2));
            long position = 0;
            while (position < length)
            {
                int frames = (int)Math.Min(blockFrames, length - position);
                Array.Clear(block, 0, frames * 2);
                foreach (var track in snapshot.Tracks)
                {
                    if (track.Muted || (anySolo && !track.Solo))
                    {
                        continue;
                    }

                    Array.Clear(trackBlock, 0, frames * 2);
                    MixTrack(track, clipPlayback, position, frames, trackBlock);
                    TrackProcessor?.Invoke(track, trackBlock, frames);
                    for (int i = 0; i < frames * 2; i++)
                    {
                        block[i] += trackBlock[i];
                    }
                }

                MasterProcessor?.Invoke(block, frames);
                writer.WriteSamples(block, 0, frames * 2);
                position += frames;
            }
        }
        catch (Exception ex)
        {
            error = "Could not bounce the timeline: " + ex.Message;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }

            return false;
        }

        return true;
    }

    public bool Undo()
    {
        lock (_gate)
        {
            if (_undoHistory.Count == 0)
            {
                return false;
            }

            var snapshot = _undoHistory[^1];
            _undoHistory.RemoveAt(_undoHistory.Count - 1);
            _redoHistory.Add(CreateSnapshot(_project));
            RestoreState(snapshot.Project);
            TrimHistory();
            return true;
        }
    }

    public bool Redo()
    {
        lock (_gate)
        {
            if (_redoHistory.Count == 0)
            {
                return false;
            }

            var snapshot = _redoHistory[^1];
            _redoHistory.RemoveAt(_redoHistory.Count - 1);
            _undoHistory.Add(CreateSnapshot(_project));
            RestoreState(snapshot.Project);
            TrimHistory();
            return true;
        }
    }

    private void Remember()
    {
        _undoHistory.Add(CreateSnapshot(_project));
        ClearRedo();
        TrimHistory();
    }

    private void ClearRedo()
    {
        foreach (var snapshot in _redoHistory)
        {
            _historyMetadataBytes -= snapshot.MetadataBytes;
            ReleaseProjectArrays(snapshot.Project);
        }

        _redoHistory.Clear();
    }

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _historyArrays.Clear();
        _historyArrayBytes = 0;
        _historyMetadataBytes = 0;
    }

    private HistorySnapshot CreateSnapshot(TimelineProject project)
    {
        var clone = CloneProject(project);
        long metadata = EstimateProjectBytes(clone);
        RegisterProjectArrays(clone);
        _historyMetadataBytes += metadata;
        return new HistorySnapshot(clone, metadata);
    }

    private void TrimHistory()
    {
        while (_undoHistory.Count > _maxUndoSteps)
        {
            RemoveOldest(_undoHistory);
        }

        while (_redoHistory.Count > _maxUndoSteps)
        {
            RemoveOldest(_redoHistory);
        }

        while (_historyMetadataBytes + _historyArrayBytes > _maxUndoBytes &&
               (_undoHistory.Count > 0 || _redoHistory.Count > 0))
        {
            if (_undoHistory.Count > 0) RemoveOldest(_undoHistory);
            else RemoveOldest(_redoHistory);
        }
    }

    private void RemoveOldest(List<HistorySnapshot> history)
    {
        var snapshot = history[0];
        history.RemoveAt(0);
        _historyMetadataBytes -= snapshot.MetadataBytes;
        ReleaseProjectArrays(snapshot.Project);
    }

    private void RegisterProjectArrays(TimelineProject project)
    {
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                RegisterArray(clip.Peaks);
                if (clip.Audio != null) RegisterArray(clip.Audio);
            }
        }
    }

    private void ReleaseProjectArrays(TimelineProject project)
    {
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                ReleaseArray(clip.Peaks);
                if (clip.Audio != null) ReleaseArray(clip.Audio);
            }
        }
    }

    private void RegisterArray(float[] array)
    {
        if (_historyArrays.TryGetValue(array, out int count))
        {
            _historyArrays[array] = count + 1;
            return;
        }

        _historyArrays[array] = 1;
        _historyArrayBytes += (long)array.Length * sizeof(float);
    }

    private void ReleaseArray(float[] array)
    {
        if (!_historyArrays.TryGetValue(array, out int count))
        {
            return;
        }

        if (count > 1)
        {
            _historyArrays[array] = count - 1;
            return;
        }

        _historyArrays.Remove(array);
        _historyArrayBytes -= (long)array.Length * sizeof(float);
    }

    private static long EstimateProjectBytes(TimelineProject project)
    {
        long bytes = 512 + ((long)project.Markers.Count * 96);
        foreach (var track in project.Tracks)
        {
            bytes += 256 + ((long)track.Automation.Count * 128);
            foreach (var lane in track.Automation)
            {
                bytes += (long)lane.Points.Count * 24;
            }

            bytes += (long)track.Clips.Count * 320;
        }

        return bytes;
    }

    private void RestoreState(TimelineProject project)
    {
        var sources = _tracks.Values.ToDictionary(item => item.Track.Id, item => item.Source);
        _project = project;
        _tracks.Clear();
        foreach (var track in project.Tracks)
        {
            var playback = new TrackPlayback(track)
            {
                Source = sources.GetValueOrDefault(track.Id),
            };
            _tracks[track.Id] = playback;
        }

        _trackSnapshot = _tracks.Values.ToArray();
        PublishRenderState();
    }

    private static TimelineProject CloneProject(TimelineProject source, bool includePeaks = true) => new()
    {
        Version = source.Version,
        SampleRate = source.SampleRate,
        Tempo = source.Tempo,
        TimeSignatureNumerator = source.TimeSignatureNumerator,
        TimeSignatureDenominator = source.TimeSignatureDenominator,
        LoopStartSample = source.LoopStartSample,
        LoopEndSample = source.LoopEndSample,
        Markers = source.Markers.Select(marker => new TimelineMarker
        {
            Id = marker.Id,
            Name = marker.Name,
            StartSample = marker.StartSample,
            EndSample = marker.EndSample,
            Color = marker.Color,
        }).ToList(),
        Tracks = source.Tracks.Select(track => new TimelineTrack
        {
            Id = track.Id,
            Name = track.Name,
            SourceNodeId = track.SourceNodeId,
            GeneratorNodeId = track.GeneratorNodeId,
            Gain = track.Gain,
            Muted = track.Muted,
            Solo = track.Solo,
            Clips = track.Clips.Select(clip => new TimelineClip
            {
                Id = clip.Id,
                Name = clip.Name,
                SourcePath = clip.SourcePath,
                StartSample = clip.StartSample,
                LengthSamples = clip.LengthSamples,
                SourceOffsetSamples = clip.SourceOffsetSamples,
                Gain = clip.Gain,
                PitchSemitones = clip.PitchSemitones,
                FadeInSamples = clip.FadeInSamples,
                FadeOutSamples = clip.FadeOutSamples,
                Loop = clip.Loop,
                Peaks = includePeaks ? clip.Peaks : Array.Empty<float>(),
                Audio = clip.Audio,
            }).ToList(),
            Automation = track.Automation.Select(lane => new TimelineAutomationLane
            {
                Id = lane.Id,
                TargetNodeId = lane.TargetNodeId,
                ProcessorIndex = lane.ProcessorIndex,
                ParameterIndex = lane.ParameterIndex,
                Points = lane.Points.Select(point => new TimelineAutomationPoint
                {
                    Sample = point.Sample,
                    Value = point.Value,
                    Curve = point.Curve,
                }).ToList(),
            }).ToList(),
        }).ToList(),
    };

    public TimelineProject SnapshotProject(bool includePeaks = true)
    {
        lock (_gate)
        {
            return CloneProject(_project, includePeaks);
        }
    }

    public bool SetTempo(double tempo)
    {
        lock (_gate)
        {
            if (!double.IsFinite(tempo))
            {
                return false;
            }

            double next = Math.Clamp(tempo, 20.0, 300.0);
            if (Math.Abs(_project.Tempo - next) < 0.001)
            {
                return true;
            }

            Remember();
            _project.Tempo = next;
            return true;
        }
    }

    public bool SetTimeSignature(int numerator, int denominator)
    {
        lock (_gate)
        {
            int nextNumerator = Math.Clamp(numerator, 1, 16);
            int nextDenominator = denominator is 1 or 2 or 4 or 8 or 16 ? denominator : 4;
            if (_project.TimeSignatureNumerator == nextNumerator &&
                _project.TimeSignatureDenominator == nextDenominator)
            {
                return true;
            }

            Remember();
            _project.TimeSignatureNumerator = nextNumerator;
            _project.TimeSignatureDenominator = nextDenominator;
            return true;
        }
    }

    public TimelineMarker AddMarker(long startSample, long? endSample, string? name)
    {
        lock (_gate)
        {
            Remember();
            long start = Math.Max(0, startSample);
            long end = endSample.HasValue && endSample.Value > start ? endSample.Value : -1;
            var marker = new TimelineMarker
            {
                Name = string.IsNullOrWhiteSpace(name)
                    ? (end > start ? $"Region {_project.Markers.Count + 1}" : $"Marker {_project.Markers.Count + 1}")
                    : name.Trim(),
                StartSample = start,
                EndSample = end,
            };
            _project.Markers.Add(marker);
            return marker;
        }
    }

    public bool UpdateMarker(string id, long? startSample, long? endSample, string? name)
    {
        lock (_gate)
        {
            var marker = _project.Markers.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal));
            if (marker == null)
            {
                return false;
            }

            Remember();
            if (startSample.HasValue)
            {
                marker.StartSample = Math.Max(0, startSample.Value);
            }

            if (endSample.HasValue)
            {
                marker.EndSample = endSample.Value > marker.StartSample ? endSample.Value : -1;
            }
            else if (marker.EndSample > 0 && marker.EndSample <= marker.StartSample)
            {
                marker.EndSample = -1;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                marker.Name = name.Trim();
            }

            return true;
        }
    }

    public bool RemoveMarker(string id)
    {
        lock (_gate)
        {
            if (!_project.Markers.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
            {
                return false;
            }

            Remember();
            _project.Markers.RemoveAll(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal));
            return true;
        }
    }

    public bool SnapshotClips(IReadOnlyList<(string TrackId, string ClipId)> items)
    {
        lock (_gate)
        {
            var clips = new List<TimelineClip>();
            foreach (var item in items)
            {
                if (!_tracks.TryGetValue(item.TrackId, out var playback))
                {
                    continue;
                }

                var source = playback.Track.Clips.FirstOrDefault(clip =>
                    string.Equals(clip.Id, item.ClipId, StringComparison.Ordinal));
                if (source != null)
                {
                    clips.Add(source);
                }
            }

            if (clips.Count == 0)
            {
                return false;
            }

            long anchor = clips.Min(clip => clip.StartSample);
            _clipboard = clips.Select(source => new TimelineClip
            {
                Name = source.Name,
                SourcePath = source.SourcePath,
                StartSample = source.StartSample - anchor,
                LengthSamples = source.LengthSamples,
                SourceOffsetSamples = source.SourceOffsetSamples,
                Gain = source.Gain,
                PitchSemitones = source.PitchSemitones,
                FadeInSamples = source.FadeInSamples,
                FadeOutSamples = source.FadeOutSamples,
                Loop = source.Loop,
                Peaks = source.Peaks,
                Audio = source.Audio,
            }).ToList();

            return true;
        }
    }

    public bool PasteClipboard(IReadOnlyList<(string TrackId, long StartSample)> placements)
    {
        lock (_gate)
        {
            if (_clipboard.Count == 0 || placements.Count == 0)
            {
                return false;
            }

            var pending = new List<(TimelineTrack Track, TimelineClip Clip)>();
            int count = Math.Min(_clipboard.Count, placements.Count);
            for (int index = 0; index < count; index++)
            {
                if (!_tracks.TryGetValue(placements[index].TrackId, out var playback))
                {
                    continue;
                }

                var source = _clipboard[index];
                pending.Add((playback.Track, new TimelineClip
                {
                    Name = source.Name,
                    SourcePath = source.SourcePath,
                    StartSample = Math.Max(0, placements[index].StartSample),
                    LengthSamples = source.LengthSamples,
                    SourceOffsetSamples = source.SourceOffsetSamples,
                    Gain = source.Gain,
                    PitchSemitones = source.PitchSemitones,
                    FadeInSamples = source.FadeInSamples,
                    FadeOutSamples = source.FadeOutSamples,
                    Loop = source.Loop,
                    Peaks = source.Peaks,
                    Audio = source.Audio,
                }));
            }

            if (pending.Count == 0)
            {
                return false;
            }

            Remember();
            foreach (var item in pending)
            {
                item.Track.Clips.Add(item.Clip);
            }

            foreach (var track in pending.Select(item => item.Track).Distinct())
            {
                if (_tracks.TryGetValue(track.Id, out var playback))
                {
                    playback.RefreshClips();
                    playback.RefreshAutomation();
                }
            }

            return true;
        }
    }
    public bool CopyClips(IReadOnlyList<(string TrackId, string ClipId, long StartSample)> copies)
    {
        lock (_gate)
        {
            var pending = new List<(TimelineTrack Track, TimelineClip Clip)>();
            foreach (var copy in copies)
            {
                if (!_tracks.TryGetValue(copy.TrackId, out var playback))
                {
                    continue;
                }

                var source = playback.Track.Clips.FirstOrDefault(item =>
                    string.Equals(item.Id, copy.ClipId, StringComparison.Ordinal));
                if (source == null)
                {
                    continue;
                }

                pending.Add((playback.Track, new TimelineClip
                {
                    Name = source.Name,
                    SourcePath = source.SourcePath,
                    StartSample = Math.Max(0, copy.StartSample),
                    LengthSamples = source.LengthSamples,
                    SourceOffsetSamples = source.SourceOffsetSamples,
                    Gain = source.Gain,
                    PitchSemitones = source.PitchSemitones,
                    FadeInSamples = source.FadeInSamples,
                    FadeOutSamples = source.FadeOutSamples,
                    Loop = source.Loop,
                    Peaks = source.Peaks,
                    Audio = source.Audio,
                }));
            }

            if (pending.Count == 0)
            {
                return false;
            }

            Remember();
            foreach (var item in pending)
            {
                item.Track.Clips.Add(item.Clip);
            }

            foreach (var track in pending.Select(item => item.Track).Distinct())
            {
                _tracks[track.Id].RefreshClips();
                _tracks[track.Id].RefreshAutomation();
            }

            return true;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            _playing = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _playing = false;
            foreach (var track in _tracks.Values)
            {
                track.ResetClipPlayback();
            }
        }
    }

    public void Seek(long sample)
    {
        lock (_gate)
        {
            _seekGeneration++;
            _playheadSample = Math.Max(0, sample);
            foreach (var track in _tracks.Values)
            {
                track.ResetClipPlayback();
            }
            if (_project.LoopEndSample > _project.LoopStartSample &&
                _playheadSample >= _project.LoopEndSample)
            {
                _playheadSample = _project.LoopStartSample;
            }
        }
    }

    public void SetLoop(long? startSample, long? endSample)
    {
        lock (_gate)
        {
            long start = startSample ?? -1;
            long end = endSample ?? -1;
            bool valid = start >= 0 && end > start;
            _project.LoopStartSample = valid ? start : -1;
            _project.LoopEndSample = valid ? end : -1;
            PublishRenderState();
        }
    }

    /// <summary>Called by the graph renderer once per block, before sources are read.</summary>
    public void RenderBlock(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        int seekGeneration = Volatile.Read(ref _seekGeneration);

        TrackPlayback[] tracks;
        long position;
        bool playing;
        long loopStart;
        long loopEnd;
        var state = _renderState;
        tracks = state.Tracks;
        position = Volatile.Read(ref _playheadSample);
        playing = _playing;
        loopStart = state.LoopStart;
        loopEnd = state.LoopEnd;

        long next = playing ? AdvancePosition(position, frames, loopStart, loopEnd) : position;
        bool anySolo = false;
        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i].Track.Solo)
            {
                anySolo = true;
                break;
            }
        }

        foreach (var track in tracks)
        {
            EnsureBlock(track, frames);
            Array.Clear(track.Block);
            bool audible = !track.Track.Muted && (!anySolo || track.Track.Solo);
            float targetGain = playing && audible ? track.Track.Gain : 0f;
            if (playing && (targetGain > 0.0001f || track.CurrentGain > 0.0001f))
            {
                long cursor = position;
                if (loopEnd > loopStart && cursor >= loopEnd)
                {
                    cursor = loopStart;
                }

                int offset = 0;
                while (offset < frames)
                {
                    int chunk = frames - offset;
                    if (loopEnd > loopStart)
                    {
                        long boundary = cursor < loopStart ? loopStart : loopEnd;
                        if (boundary > cursor)
                        {
                            chunk = Math.Min(chunk, (int)Math.Min(int.MaxValue, boundary - cursor));
                        }
                    }

                    FillTrack(track, cursor, chunk, offset, targetGain);
                    cursor += chunk;
                    offset += chunk;
                    if (loopEnd > loopStart && cursor >= loopEnd)
                    {
                        cursor = loopStart;
                    }
                }

            }

            track.Source?.PublishTimelineBlock(track.Block);
            if (track.Track.GeneratorNodeId is { } generatorNodeId)
            {
                bool generatorActive = playing && audible && targetGain > 0.0001f;
                if (generatorActive)
                {
                    generatorActive = false;
                    for (int clipIndex = 0; clipIndex < track.Clips.Length; clipIndex++)
                    {
                        var clip = track.Clips[clipIndex];
                        if (position >= clip.StartSample &&
                            position < clip.StartSample + clip.LengthSamples)
                        {
                            generatorActive = true;
                            break;
                        }
                    }
                }

                ApplyGeneratorGate?.Invoke(generatorNodeId, generatorActive ? track.Track.Gain : 0f);
            }
        }

        if (playing)
        {
            foreach (var track in tracks)
            {
                if (track.Track.Muted || (anySolo && !track.Track.Solo))
                {
                    continue;
                }

                foreach (var binding in track.Automation)
                {
                    var lane = binding.Lane;
                    if (lane.Points.Count == 0)
                    {
                        continue;
                    }

                    float value = EvaluateAutomation(lane.Points, position);
                    if (binding.ClipPitch is { } clip)
                    {
                        clip.PitchAutomationSemitones = Math.Clamp(value,
                            -MaxClipPitchSemitones, MaxClipPitchSemitones);
                        continue;
                    }

                    try
                    {
                        ApplyAutomation?.Invoke(lane.TargetNodeId, lane.ProcessorIndex,
                            lane.ParameterIndex, value);
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (!playing)
        {
            return;
        }

        if (Volatile.Read(ref _seekGeneration) == seekGeneration)
        {
            Interlocked.Exchange(ref _playheadSample, next);
        }
    }

    private static List<TimelineAutomationPoint> NormalizeAutomationPoints(
        IEnumerable<TimelineAutomationPoint> points)
    {
        return points
            .Where(point => float.IsFinite(point.Value))
            .GroupBy(point => Math.Max(0, point.Sample))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var point = group.Last();
                return new TimelineAutomationPoint
                {
                    Sample = Math.Max(0, point.Sample),
                    Value = point.Value,
                    Curve = point.Curve,
                };
            })
            .ToList();
    }

    private static float EvaluateAutomation(IReadOnlyList<TimelineAutomationPoint> points, long sample)
    {
        if (points.Count == 0)
        {
            return 0f;
        }

        if (sample <= points[0].Sample)
        {
            return points[0].Value;
        }

        for (int i = 1; i < points.Count; i++)
        {
            var next = points[i];
            if (sample > next.Sample)
            {
                continue;
            }

            var previous = points[i - 1];
            long span = Math.Max(1, next.Sample - previous.Sample);
            float t = Math.Clamp((sample - previous.Sample) / (float)span, 0f, 1f);
            if (previous.Curve == TimelineAutomationCurve.Step)
            {
                return previous.Value;
            }

            if (previous.Curve == TimelineAutomationCurve.Exponential)
            {
                t = t * t;
            }

            return previous.Value + (next.Value - previous.Value) * t;
        }

        return points[^1].Value;
    }

    private static long AdvancePosition(long position, int frames, long loopStart, long loopEnd)
    {
        long cursor = position;
        if (loopEnd > loopStart && cursor >= loopEnd)
        {
            cursor = loopStart;
        }

        int offset = 0;
        while (offset < frames)
        {
            int chunk = frames - offset;
            if (loopEnd > loopStart)
            {
                long boundary = cursor < loopStart ? loopStart : loopEnd;
                if (boundary > cursor)
                {
                    chunk = Math.Min(chunk, (int)Math.Min(int.MaxValue, boundary - cursor));
                }
            }

            cursor += chunk;
            offset += chunk;
            if (loopEnd > loopStart && cursor >= loopEnd)
            {
                cursor = loopStart;
            }
        }

        return cursor;
    }

    private static void EnsureBlock(TrackPlayback track, int frames)
    {
        int needed = frames * 2;
        if (track.Block.Length != needed)
        {
            track.Block = new float[needed];
        }
    }

    private static void FillTrack(TrackPlayback playback, long position, int frames,
        int blockOffset, float targetGain)
    {
        var snapshot = playback.ClipSnapshot;
        for (int i = 0; i < snapshot.Clips.Length; i++)
        {
            MixClipInto(snapshot.Clips[i], snapshot.Playback[i], 1f,
                position, frames, playback.Block, blockOffset);
        }

        int start = blockOffset * 2;
        int end = Math.Min(playback.Block.Length, (blockOffset + frames) * 2);
        float gain = playback.CurrentGain;
        for (int i = start; i < end; i++)
        {
            gain += (targetGain - gain) * ParameterSmoothing.FastAlpha;
            playback.Block[i] *= gain;
        }

        playback.CurrentGain = gain;
    }
    private static float[] ComputePeaks(float[] audio)
    {
        const int count = 512;
        var peaks = new float[count];
        int frames = audio.Length / 2;
        if (frames <= 0)
        {
            return peaks;
        }

        for (int i = 0; i < count; i++)
        {
            int start = (int)((long)i * frames / count);
            int end = (int)((long)(i + 1) * frames / count);
            float peak = 0;
            for (int frame = start; frame < end; frame++)
            {
                peak = Math.Max(peak, Math.Max(Math.Abs(audio[frame * 2]), Math.Abs(audio[(frame * 2) + 1])));
            }

            peaks[i] = peak;
        }

        return peaks;
    }

    private static float[] LoadClip(string path)
    {
        if (!TrustedPath.IsLocalFile(path))
        {
            throw new InvalidOperationException("The clip path must be a local file.");
        }

        using var reader = new AudioFileReader(path);
        ISampleProvider sampleProvider = reader;
        var converter = new FormatConverter(reader.WaveFormat.Channels, reader.WaveFormat.SampleRate);
        var converted = new List<float>();
        var source = new float[8192 * Math.Max(1, reader.WaveFormat.Channels)];
        int read;
        while ((read = sampleProvider.Read(source.AsSpan())) > 0)
        {
            if (converted.Count >= MaxClipSamples * 2)
            {
                throw new InvalidOperationException("The clip is longer than the 10 minute MVP limit.");
            }

            converter.Convert(source.AsSpan(0, read), converted);
        }

        if (converted.Count > MaxClipSamples * 2)
        {
            converted.RemoveRange(MaxClipSamples * 2, converted.Count - MaxClipSamples * 2);
        }

        return converted.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _playing = false;
            _tracks.Clear();
        }
    }

    private sealed class TrackPlayback
    {
        private ClipSnapshot _clipSnapshot = ClipSnapshot.Empty;
        private AutomationBinding[] _automation = Array.Empty<AutomationBinding>();

        public TrackPlayback(TimelineTrack track)
        {
            Track = track;
            CurrentGain = track.Gain;
            RefreshClips();
            RefreshAutomation();
        }

        public TimelineTrack Track { get; }
        public SourceNode? Source { get; set; }
        public float[] Block { get; set; } = Array.Empty<float>();
        public float CurrentGain { get; set; } = 1f;
        public ClipSnapshot ClipSnapshot => _clipSnapshot;
        public TimelineClip[] Clips => _clipSnapshot.Clips;
        public AutomationBinding[] Automation => _automation;

        public void RefreshClips()
        {
            var previous = _clipSnapshot;
            var existing = new Dictionary<string, ClipPlayback>(StringComparer.Ordinal);
            for (int i = 0; i < previous.Clips.Length && i < previous.Playback.Length; i++)
            {
                existing[previous.Clips[i].Id] = previous.Playback[i];
            }

            var clips = Track.Clips.ToArray();
            var playback = new ClipPlayback[clips.Length];
            for (int i = 0; i < clips.Length; i++)
            {
                playback[i] = existing.TryGetValue(clips[i].Id, out var state)
                    ? state
                    : new ClipPlayback();
            }

            _clipSnapshot = new ClipSnapshot(clips, playback);
        }

        public void ResetClipPlayback()
        {
            foreach (var state in _clipSnapshot.Playback)
            {
                state.Reset();
            }
        }

        public void RefreshAutomation()
        {
            var clips = new Dictionary<string, TimelineClip>(StringComparer.Ordinal);
            foreach (var clip in _clipSnapshot.Clips)
            {
                clips[clip.Id] = clip;
            }

            var automated = new HashSet<string>(StringComparer.Ordinal);
            var bindings = new AutomationBinding[Track.Automation.Count];
            for (int i = 0; i < Track.Automation.Count; i++)
            {
                var lane = Track.Automation[i];
                TimelineClip? clip = null;
                if (lane.TargetNodeId.StartsWith(ClipPitchAutomationPrefix, StringComparison.Ordinal))
                {
                    string clipId = lane.TargetNodeId[ClipPitchAutomationPrefix.Length..];
                    clips.TryGetValue(clipId, out clip);
                    if (clip != null)
                    {
                        automated.Add(clipId);
                    }
                }

                bindings[i] = new AutomationBinding(lane, clip);
            }

            foreach (var clip in _clipSnapshot.Clips)
            {
                if (!automated.Contains(clip.Id))
                {
                    clip.PitchAutomationSemitones = null;
                }
            }

            _automation = bindings;
        }
    }

    private sealed record AutomationBinding(TimelineAutomationLane Lane, TimelineClip? ClipPitch);

    private sealed record ClipSnapshot(TimelineClip[] Clips, ClipPlayback[] Playback)
    {
        public static readonly ClipSnapshot Empty =
            new(Array.Empty<TimelineClip>(), Array.Empty<ClipPlayback>());
    }

}
