using System.IO;
using NAudio.Wave;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    /// <summary>
    /// Offline graph used by timeline bounce/freeze. It clones the live
    /// timeline, effect chains and the destination output, then renders the
    /// whole routing graph one block at a time on a virtual clock.
    /// </summary>
    private sealed class OfflineTimelineBounce : IDisposable
    {
        public AudioGraph Graph { get; } = new() { AutoRun = false };
        public TimelineService Timeline { get; } = new();
        public required OutputNode Output { get; init; }
        public bool SkippedVst3 { get; set; }
        public bool SkippedProcessor { get; set; }
        public double TailSeconds { get; set; } = 2.0;
        public long StartSample { get; set; }
        public long EndSample { get; set; } = -1;
        public Action<double>? Progress { get; set; }
        public CancellationToken CancelToken { get; set; }

        public bool Render(string path, out string error)
        {
            error = "";
            long totalLength = Timeline.TotalLengthSamples();
            long start = Math.Clamp(StartSample, 0, Math.Max(0, totalLength));
            long end = EndSample > start ? Math.Min(EndSample, totalLength) : totalLength;
            long length = end - start;
            if (length <= 0)
            {
                error = "The timeline range has no audio to bounce.";
                return false;
            }

            // Let delay and reverb tails finish naturally.
            length += (long)Math.Round(Math.Clamp(TailSeconds, 0, 30) * AudioEngineConfig.SampleRate);
            Timeline.SetLoop(null, null);
            Timeline.Seek(start);
            Timeline.Play();

            try
            {
                const int channels = 2;
                int blockFrames = AudioEngineConfig.BlockFrames;
                var block = new float[blockFrames * channels];
                var output = new float[blockFrames * channels];
                using var writer = new WaveFileWriter(
                    path, new WaveFormat(AudioEngineConfig.SampleRate, 24, channels));
                long position = 0;
                long lastProgress = -1;
                while (position < length)
                {
                    if (CancelToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    Graph.RenderOfflineBlock();
                    int read = Output.ReadOfflineBlock(output);
                    if (read < output.Length)
                    {
                        Array.Clear(output, read, output.Length - read);
                    }

                    writer.WriteSamples(output, 0, output.Length);
                    position += blockFrames;
                    long progress = (long)(Math.Clamp(position / (double)length, 0, 1) * 100);
                    if (progress != lastProgress)
                    {
                        lastProgress = progress;
                        Progress?.Invoke(progress / 100.0);
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                error = "Timeline bounce canceled.";
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                error = "Could not bounce the timeline: " + ex.Message;
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                }

                return false;
            }
        }

        public void Dispose()
        {
            Timeline.Stop();
            Timeline.Dispose();
            Graph.Dispose();
        }
    }

    private float[] RenderGeneratorTrack(TimelineTrack track, SourceNode source, long length)
    {
        var temporary = new SourceNode(source.Title, source.Kind);
        if (source.IsMetronome)
        {
            temporary.SetMetronome(source.MetronomeBpm, source.MetronomeBeatsPerBar);
        }
        else if (source.Kind == SourceKind.Synth)
        {
            temporary.SetSynth(source.Synth);
        }
        else
        {
            temporary.SetTone(source.ToneFrequency, source.ToneWave, source.ToneLevelDb);
        }

        var parameters = SourceAutomationParameters(temporary);
        using var renderer = new GeneratorOfflineRenderer(temporary);
        var result = new float[length * 2];
        const int blockFrames = 1024;
        int offset = 0;
        while (offset < length)
        {
            int frames = (int)Math.Min(blockFrames, length - offset);
            foreach (var lane in track.Automation)
            {
                if (lane.TargetNodeId == track.GeneratorNodeId &&
                    lane.ProcessorIndex == -1 &&
                    lane.ParameterIndex >= 0 &&
                    lane.ParameterIndex < parameters.Count)
                {
                    parameters[lane.ParameterIndex].Set(EvaluateGeneratorAutomation(lane.Points, offset));
                }
            }

            renderer.Update(temporary);
            var block = renderer.Render(frames);
            bool active = track.Clips.Any(clip =>
                offset >= clip.StartSample && offset < clip.StartSample + clip.LengthSamples);
            if (active)
            {
                Array.Copy(block, 0, result, offset * 2, frames * 2);
            }

            offset += frames;
        }

        return result;
    }

    private static float EvaluateGeneratorAutomation(IReadOnlyList<TimelineAutomationPoint> points, long sample)
    {
        if (points.Count == 0) return 0f;
        if (sample <= points[0].Sample) return points[0].Value;
        for (int index = 1; index < points.Count; index++)
        {
            var next = points[index];
            if (sample > next.Sample) continue;
            var previous = points[index - 1];
            if (next.Sample == previous.Sample) return next.Value;
            float amount = (float)((sample - previous.Sample) / (double)(next.Sample - previous.Sample));
            return previous.Curve switch
            {
                TimelineAutomationCurve.Step => previous.Value,
                TimelineAutomationCurve.Exponential => previous.Value + (next.Value - previous.Value) * amount * amount,
                _ => previous.Value + (next.Value - previous.Value) * amount,
            };
        }

        return points[^1].Value;
    }

    private OfflineTimelineBounce? BuildTimelineBounceGraph()
    {
        if (_timeline == null)
        {
            return null;
        }

        string preferredOutput = _settings.TimelineBounceOutputId;
        NodeEntry? targetEntry = !string.IsNullOrWhiteSpace(preferredOutput)
            ? _nodes.Values.FirstOrDefault(entry =>
                entry.Output is { IsFileRecorder: false, IsNetworkOutput: false } &&
                string.Equals(entry.Output.DeviceId, preferredOutput, StringComparison.OrdinalIgnoreCase))
            : null;
        targetEntry ??= _nodes.Values.FirstOrDefault(entry =>
            entry.Output is { IsFileRecorder: false, IsNetworkOutput: false });
        if (targetEntry?.Output == null)
        {
            return null;
        }

        var bounce = new OfflineTimelineBounce
        {
            Output = OutputNode.CreateForTest(static (_, _) => { }),
        };
        bounce.Output.EnableGraphEngine();
        bounce.Output.Volume = targetEntry.Output.Volume;
        bounce.Output.Muted = targetEntry.Output.Muted;
        bounce.Output.GroupKey = targetEntry.Output.GroupKey;
        bounce.TailSeconds = Math.Clamp(_settings.TimelineBounceTailSeconds, 0, 30);

        try
        {
            var timelineProject = _timeline.SnapshotProject(includePeaks: false);
            bounce.Timeline.LoadProject(timelineProject);
            bounce.Graph.Timeline = bounce.Timeline;
            bounce.Graph.AddOutputNode(bounce.Output);

            var sources = new Dictionary<string, SourceNode>(StringComparer.Ordinal);
            foreach (var track in bounce.Timeline.Project.Tracks)
            {
                if (string.IsNullOrWhiteSpace(track.GeneratorNodeId) || track.Clips.Count == 0 ||
                    !_nodes.TryGetValue(track.GeneratorNodeId, out var generatorEntry) ||
                    generatorEntry.Source is not { } generatorSource)
                {
                    continue;
                }

                long end = track.Clips.Max(clip => clip.StartSample + clip.LengthSamples);
                if (end <= 0) continue;
                var generatedAudio = RenderGeneratorTrack(track, generatorSource, end);
                track.Clips.Clear();
                track.Clips.Add(new TimelineClip
                {
                    Name = generatorSource.Title,
                    StartSample = 0,
                    LengthSamples = end,
                    Audio = generatedAudio,
                    Peaks = Array.Empty<float>(),
                });
                var generatedSource = new SourceNode(track.Name, SourceKind.Timeline);
                bounce.Graph.AddSource(generatedSource);
                bounce.Timeline.AttachSource(track.Id, generatedSource);
                sources[track.GeneratorNodeId] = generatedSource;
                generatedSource.Start();
            }


            foreach (var track in bounce.Timeline.Project.Tracks)
            {
                if (string.IsNullOrWhiteSpace(track.SourceNodeId) ||
                    !_nodes.TryGetValue(track.SourceNodeId, out var live) ||
                    live.Source is not { Kind: SourceKind.Timeline } liveSource)
                {
                    continue;
                }

                var source = new SourceNode(track.Name, SourceKind.Timeline)
                {
                    Gain = liveSource.Gain,
                    Muted = liveSource.Muted,
                    GroupKey = liveSource.GroupKey,
                };
                bounce.Graph.AddSource(source);
                bounce.Timeline.AttachSource(track.Id, source);
                sources[live.Id] = source;
                source.Start();
            }

            var modifiers = new Dictionary<string, ModifierNode>(StringComparer.Ordinal);
            foreach (var live in _nodes.Values.Where(entry => entry.Modifier != null))
            {
                var sourceModifier = live.Modifier!;
                var modifier = new ModifierNode(live.Title)
                {
                    Enabled = sourceModifier.Enabled,
                    Parallel = sourceModifier.Parallel,
                    Send = sourceModifier.Send,
                };

                foreach (var processor in sourceModifier.Processors)
                {
                    if (processor is ITapProcessor) continue;
                    if (processor is IVst3Processor)
                    {
                        bounce.SkippedVst3 = true;
                        continue;
                    }

                    try
                    {
                        var clone = PresetSerializer.CreateProcessor(
                            PresetSerializer.SerializeProcessor(processor));
                        if (clone != null) modifier.Add(clone);
                    }
                    catch
                    {
                        bounce.SkippedProcessor = true;
                    }
                }

                bounce.Graph.AddModifier(modifier);
                modifiers[live.Id] = modifier;
            }

            foreach (var link in _links)
            {
                if (!link.Enabled) continue;

                if (link.Kind == "direct" &&
                    sources.TryGetValue(link.From, out var directSource) &&
                    link.To == targetEntry.Id)
                {
                    bounce.Graph.Connect(directSource, bounce.Output, link.Gain);
                }
                else if (link.Kind == "source-modifier" &&
                         sources.TryGetValue(link.From, out var linkedSource) &&
                         modifiers.TryGetValue(link.To, out var linkedModifier))
                {
                    bounce.Graph.ConnectSourceToModifier(linkedSource, linkedModifier);
                }
                else if (link.Kind == "sidechain")
                {
                    if (sources.TryGetValue(link.From, out var keySource) &&
                        modifiers.TryGetValue(link.To, out var keyModifier))
                    {
                        bounce.Graph.ConnectSourceToModifierSidechain(keySource, keyModifier);
                    }
                    else if (modifiers.TryGetValue(link.From, out var keyModifierSource) &&
                             modifiers.TryGetValue(link.To, out var keyModifierTarget))
                    {
                        bounce.Graph.ConnectModifierToModifierSidechain(keyModifierSource, keyModifierTarget);
                    }
                }
                else if (link.Kind == "modifier-output" &&
                         link.To == targetEntry.Id &&
                         modifiers.TryGetValue(link.From, out var outputModifier))
                {
                    bounce.Graph.ConnectModifierToOutput(outputModifier, bounce.Output, link.Gain);
                }
                else if (link.Kind == "modifier-modifier" &&
                         modifiers.TryGetValue(link.From, out var fromModifier) &&
                         modifiers.TryGetValue(link.To, out var toModifier))
                {
                    bounce.Graph.ConnectModifierToModifier(fromModifier, toModifier);
                }
            }

            return bounce;
        }
        catch
        {
            bounce.Dispose();
            throw;
        }
    }
}
