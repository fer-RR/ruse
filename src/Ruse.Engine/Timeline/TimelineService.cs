using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Sample-addressed timeline playback for the existing graph. The render thread
/// calls <see cref="RenderBlock"/> once per graph block; track sources publish
/// exactly the frames for that block, so clip starts and seeks are sample
/// accurate relative to the engine clock.
/// </summary>
public sealed class TimelineService : IDisposable
{
    private const int MaxClipSamples = 48_000 * 60 * 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, TrackPlayback> _tracks = new();
    private TrackPlayback[] _trackSnapshot = Array.Empty<TrackPlayback>();
    private TimelineProject _project = new();
    private long _playheadSample;
    private bool _playing;
    private readonly Stack<TimelineProject> _undo = new();
    private readonly Stack<TimelineProject> _redo = new();

    public TimelineProject Project => _project;
    public int SampleRate => AudioEngineConfig.SampleRate;
    public long PlayheadSample { get { lock (_gate) return _playheadSample; } }
    public bool IsPlaying { get { lock (_gate) return _playing; } }
    public bool HasTracks { get { lock (_gate) return _tracks.Count > 0; } }
    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }
    public Action<string, int, int, float>? ApplyAutomation { get; set; }

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
                    audio[i] = !string.IsNullOrWhiteSpace(clip.SourcePath) && File.Exists(clip.SourcePath)
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
            _undo.Clear();
            _redo.Clear();
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
            _redo.Clear();
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

    public bool AddClip(string trackId, string path, out string error, long? startSample = null)
    {
        error = "";
        float[]? audio;
        try
        {
            audio = LoadClip(path);
        }
        catch (Exception ex)
        {
            error = "Could not load the clip: " + ex.Message;
            return false;
        }

        if (audio.Length == 0)
        {
            error = "The clip contains no audio.";
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
        float? gain, int? fadeInSamples, int? fadeOutSamples)
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
                clip.LengthSamples = Math.Clamp(lengthSamples.Value, 1, available);
            }

            if (gain.HasValue) clip.Gain = Math.Clamp(gain.Value, 0f, 4f);
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

            Remember();
            from.Track.Clips.Remove(clip);
            clip.StartSample = Math.Max(0, startSample);
            to.Track.Clips.Add(clip);
            return true;
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
                    MixTrack(track, position, frames, trackBlock);
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

    private static void MixTrack(TimelineTrack track, long position, int frames, float[] block)
    {
        foreach (var clip in track.Clips)
        {
            MixClipInto(clip, track.Gain, position, frames, block, 0);
        }
    }

    private static void MixClipInto(TimelineClip clip, float trackGain, long position, int frames,
        float[] block, int blockOffset)
    {
        if (clip.Audio == null || clip.LengthSamples <= 0)
        {
            return;
        }

        long clipEnd = clip.StartSample + clip.LengthSamples;
        long blockEnd = position + frames;
        if (blockEnd <= clip.StartSample || position >= clipEnd)
        {
            return;
        }

        int writeOffset = (int)Math.Max(0, clip.StartSample - position);
        int writeFrames = (int)Math.Min(frames - writeOffset,
            Math.Min(clipEnd, blockEnd) - Math.Max(position, clip.StartSample));
        long sourceFrame = Math.Max(0, clip.SourceOffsetSamples + Math.Max(0, position - clip.StartSample));
        if (writeFrames <= 0 || sourceFrame * 2 >= clip.Audio.Length)
        {
            return;
        }

        writeFrames = Math.Min(writeFrames, (clip.Audio.Length / 2) - (int)sourceFrame);
        int sourceIndex = (int)sourceFrame * 2;
        int targetIndex = (blockOffset + writeOffset) * 2;
        int fadeIn = Math.Max(0, clip.FadeInSamples);
        int fadeOut = Math.Max(0, clip.FadeOutSamples);
        long clipFrame = sourceFrame - clip.SourceOffsetSamples;
        for (int frame = 0; frame < writeFrames; frame++)
        {
            float gain = clip.Gain * trackGain;
            if (fadeIn > 0 && clipFrame + frame < fadeIn)
            {
                gain *= (clipFrame + frame) / (float)fadeIn;
            }

            long tail = clip.LengthSamples - (clipFrame + frame);
            if (fadeOut > 0 && tail < fadeOut)
            {
                gain *= Math.Max(0f, tail / (float)fadeOut);
            }

            block[targetIndex] += clip.Audio[sourceIndex] * gain;
            block[targetIndex + 1] += clip.Audio[sourceIndex + 1] * gain;
            sourceIndex += 2;
            targetIndex += 2;
        }
    }
    public bool Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0)
            {
                return false;
            }

            _redo.Push(CloneProject(_project));
            RestoreState(_undo.Pop());
            return true;
        }
    }

    public bool Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0)
            {
                return false;
            }

            _undo.Push(CloneProject(_project));
            RestoreState(_redo.Pop());
            return true;
        }
    }

    private void Remember()
    {
        _undo.Push(CloneProject(_project));
        if (_undo.Count > 100)
        {
            var keep = _undo.ToArray().Take(100).Reverse().ToArray();
            _undo.Clear();
            foreach (var project in keep)
            {
                _undo.Push(project);
            }
        }

        _redo.Clear();
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
    }

    private static TimelineProject CloneProject(TimelineProject source, bool includePeaks = true) => new()
    {
        Version = source.Version,
        SampleRate = source.SampleRate,
        Tempo = source.Tempo,
        LoopStartSample = source.LoopStartSample,
        LoopEndSample = source.LoopEndSample,
        Tracks = source.Tracks.Select(track => new TimelineTrack
        {
            Id = track.Id,
            Name = track.Name,
            SourceNodeId = track.SourceNodeId,
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
                FadeInSamples = clip.FadeInSamples,
                FadeOutSamples = clip.FadeOutSamples,
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
        }
    }

    public void Seek(long sample)
    {
        lock (_gate)
        {
            _playheadSample = Math.Max(0, sample);
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
        }
    }

    /// <summary>Called by the graph renderer once per block, before sources are read.</summary>
    public void RenderBlock(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        TrackPlayback[] tracks;
        long position;
        bool playing;
        long loopStart;
        long loopEnd;
        lock (_gate)
        {
            tracks = _trackSnapshot;
            position = _playheadSample;
            playing = _playing;
            loopStart = _project.LoopStartSample;
            loopEnd = _project.LoopEndSample;
        }

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
        }

        if (playing)
        {
            foreach (var track in tracks)
            {
                if (track.Track.Muted || (anySolo && !track.Track.Solo))
                {
                    continue;
                }

                foreach (var lane in track.Automation)
                {
                    if (lane.Points.Count == 0)
                    {
                        continue;
                    }

                    try
                    {
                        ApplyAutomation?.Invoke(lane.TargetNodeId, lane.ProcessorIndex,
                            lane.ParameterIndex, EvaluateAutomation(lane.Points, position));
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

        lock (_gate)
        {
            _playheadSample = next;
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
        foreach (var clip in playback.Clips)
        {
            MixClipInto(clip, 1f, position, frames, playback.Block, blockOffset);
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
        private TimelineClip[] _clips = Array.Empty<TimelineClip>();
        private TimelineAutomationLane[] _automation = Array.Empty<TimelineAutomationLane>();

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
        public TimelineClip[] Clips => _clips;
        public TimelineAutomationLane[] Automation => _automation;

        public void RefreshClips() => _clips = Track.Clips.ToArray();
        public void RefreshAutomation() => _automation = Track.Automation.ToArray();
    }
}
