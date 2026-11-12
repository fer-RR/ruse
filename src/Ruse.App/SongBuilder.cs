using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>
/// Builds a short, arranged song from the sample library, renders it through
/// Ruse's own effects, and saves a loadable routing preset plus the timeline
/// project. Used by the --make-song diagnostic mode.
/// </summary>
internal static class SongBuilder
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const double Bpm = 92.0;
    private static readonly long Beat = (long)Math.Round(60.0 / Bpm * SampleRate);
    private static readonly long Bar = Beat * 4;

    public sealed record Result(string AudioPath, string PresetPath, string ProjectPath, string Summary);

    public static Result Create(string outputDirectory)
    {
        NaudioEffects.RegisterAll();
        Directory.CreateDirectory(outputDirectory);
        string clipDirectory = Path.Combine(outputDirectory, "Clips");
        Directory.CreateDirectory(clipDirectory);

        string samples = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Ruse Samples");
        if (!Directory.Exists(samples))
        {
            throw new DirectoryNotFoundException($"Sample folder not found: {samples}");
        }

        var library = new SampleLibrary(samples, clipDirectory);
        var timeline = new TimelineService();
        var tracks = new Dictionary<string, TimelineTrack>(StringComparer.Ordinal);
        var sourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var chains = new Dictionary<string, List<PresetProcessor>>(StringComparer.Ordinal);

        TimelineTrack AddTrack(string name)
        {
            var track = timeline.AddTrack(name);
            var source = new SourceNode(name, SourceKind.Timeline);
            timeline.AttachSource(track.Id, source);
            tracks[name] = track;
            sourceIds[name] = source.Id;
            return track;
        }

        TimelineTrack piano = AddTrack("Piano");
        TimelineTrack pad = AddTrack("Strings");
        TimelineTrack bass = AddTrack("Bass");
        TimelineTrack melodyTrack = AddTrack("Flute");
        TimelineTrack drums = AddTrack("Drums");
        TimelineTrack harp = AddTrack("Harp");

        void AddClip(TimelineTrack track, string path, long start, long? length,
            float gain, int fadeIn, int fadeOut)
        {
            if (!timeline.AddClip(track.Id, path, out string error, start))
            {
                throw new InvalidOperationException(error);
            }

            var clip = track.Clips[^1];
            timeline.UpdateClip(track.Id, clip.Id, start, length, 0, gain, fadeIn, fadeOut);
            clip.Name = Path.GetFileNameWithoutExtension(path);
        }

        long At(double beats) => (long)Math.Round(beats * Beat);
        int Ms(double milliseconds) => (int)Math.Round(milliseconds * SampleRate / 1000.0);

        // --- harmony -------------------------------------------------------
        var chords = new (string Name, int[] Notes)[]
        {
            ("Am", new[] { 57, 60, 64 }),
            ("F", new[] { 53, 57, 60 }),
            ("C", new[] { 60, 64, 67 }),
            ("G", new[] { 55, 59, 62 }),
        };
        string[] progression = { "Am", "F", "C", "G", "Am", "F", "C", "G", "F", "G", "Am", "Am" };

        var pianoChords = new Dictionary<string, string>(StringComparer.Ordinal);
        var padChords = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chord in chords)
        {
            pianoChords[chord.Name] = library.RenderChord(
                "piano-" + chord.Name, "SplendidPiano-C4.wav", 60, chord.Notes,
                Bar + Ms(120), loop: false, noteGain: 0.28f, attackMs: 4, releaseMs: 260);
            padChords[chord.Name] = library.RenderChord(
                "pad-" + chord.Name, "Alesis-Fusion-Tremolo-Strings-C4.wav", 60, chord.Notes,
                Bar + Ms(260), loop: true, noteGain: 0.22f, attackMs: 180, releaseMs: 420);
        }

        var bassNotes = new Dictionary<int, string>();
        var melodyNotes = new Dictionary<int, string>();
        var harpNotes = new Dictionary<int, string>();
        string BassNote(int midi) => bassNotes.TryGetValue(midi, out var p)
            ? p
            : bassNotes[midi] = library.RenderNote(
                $"bass-{midi}", "Alesis-Fusion-Fretless-Bass-C3.wav", 48, midi,
                Ms(2200), loop: false, noteGain: 0.5f, attackMs: 4, releaseMs: 320);
        string MelodyNote(int midi) => melodyNotes.TryGetValue(midi, out var p)
            ? p
            : melodyNotes[midi] = library.RenderNote(
                $"flute-{midi}", "1980s-Casio-Flute-C5.wav", 72, midi,
                Ms(1800), loop: false, noteGain: 0.55f, attackMs: 30, releaseMs: 300);
        string HarpNote(int midi) => harpNotes.TryGetValue(midi, out var p)
            ? p
            : harpNotes[midi] = library.RenderNote(
                $"harp-{midi}", "ConcertHarp-A4.wav", 69, midi,
                Ms(1600), loop: false, noteGain: 0.45f, attackMs: 3, releaseMs: 360);

        // --- arrangement ---------------------------------------------------
        for (int bar = 0; bar < progression.Length; bar++)
        {
            string chord = progression[bar];
            long barStart = bar * Bar;

            AddClip(pad, padChords[chord], barStart, Bar + Ms(180), 0.42f, Ms(20), Ms(380));

            if (bar < 2)
            {
                // Sparse piano arpeggio over the intro.
                int[] arp = { 0, 1, 2, 1, 2, 1, 2, 1 };
                int[] chordNotes = chords.First(c => c.Name == chord).Notes;
                for (int step = 0; step < 8; step++)
                {
                    int midi = chordNotes[arp[step]] + 12;
                    AddClip(piano, library.RenderNote(
                            $"piano-arp-{midi}", "SplendidPiano-C4.wav", 60, midi,
                            Ms(1200), loop: false, noteGain: 0.42f, attackMs: 3, releaseMs: 220),
                        barStart + At(step * 0.5), At(0.9), 0.7f, Ms(3), Ms(120));
                }
            }
            else if (bar < progression.Length - 1)
            {
                AddClip(piano, pianoChords[chord], barStart, At(1.35), 0.95f, Ms(3), Ms(140));
                AddClip(piano, pianoChords[chord], barStart + At(2.5), At(1.1), 0.62f, Ms(3), Ms(120));
            }
            else
            {
                AddClip(piano, pianoChords[chord], barStart, Bar + Ms(200), 0.85f, Ms(3), Ms(900));
            }
        }

        // Bass: root, root, fifth pattern under the groove.
        int[] bassRoots = { 33, 29, 36, 31, 33, 29, 36, 31, 29, 31, 33, 33 };
        for (int bar = 2; bar < 11; bar++)
        {
            int root = bassRoots[bar];
            long barStart = bar * Bar;
            AddClip(bass, BassNote(root), barStart, At(1.5), 0.9f, Ms(3), Ms(90));
            AddClip(bass, BassNote(root), barStart + At(2.0), At(1.0), 0.7f, Ms(3), Ms(70));
            AddClip(bass, BassNote(root + 7), barStart + At(3.5), At(0.5), 0.5f, Ms(3), Ms(60));
        }

        AddClip(bass, BassNote(33), 11 * Bar, Bar, 0.8f, Ms(3), Ms(900));

        // Flute melody, one phrase per bar.
        var melodyPattern = new (int Bar, double Beat, int Midi, double Beats, float Gain)[]
        {
            (2, 0.0, 76, 1.0, 0.9f), (2, 1.0, 79, 1.0, 0.85f), (2, 2.0, 76, 1.0, 0.9f), (2, 3.0, 74, 1.0, 0.8f),
            (3, 0.0, 74, 1.5, 0.9f), (3, 1.5, 71, 0.5, 0.7f), (3, 2.0, 67, 1.0, 0.75f), (3, 3.0, 74, 1.0, 0.85f),
            (4, 0.0, 69, 1.0, 0.85f), (4, 1.0, 72, 1.0, 0.9f), (4, 2.0, 76, 2.0, 0.95f),
            (5, 0.0, 69, 1.0, 0.8f), (5, 1.0, 72, 1.0, 0.85f), (5, 2.0, 69, 1.0, 0.8f), (5, 3.0, 67, 1.0, 0.75f),
            (6, 0.0, 67, 1.0, 0.75f), (6, 1.0, 76, 1.0, 0.9f), (6, 2.0, 74, 1.0, 0.85f), (6, 3.0, 72, 1.0, 0.8f),
            (7, 0.0, 74, 2.0, 0.9f), (7, 2.0, 71, 1.0, 0.8f), (7, 3.0, 67, 1.0, 0.75f),
            (8, 0.0, 69, 1.0, 0.85f), (8, 1.0, 72, 1.0, 0.9f), (8, 2.0, 74, 1.0, 0.9f), (8, 3.0, 76, 1.0, 0.95f),
            (9, 0.0, 74, 1.5, 0.9f), (9, 1.5, 71, 0.5, 0.7f), (9, 2.0, 74, 1.0, 0.85f), (9, 3.0, 79, 1.0, 0.9f),
            (10, 0.0, 69, 2.0, 0.9f), (10, 2.0, 72, 1.0, 0.8f), (10, 3.0, 76, 1.0, 0.85f),
        };
        foreach (var note in melodyPattern)
        {
            AddClip(melodyTrack, MelodyNote(note.Midi),
                note.Bar * Bar + At(note.Beat), (long)(note.Beats * Beat * 0.92),
                note.Gain, Ms(12), Ms(70));
        }

        // Harp sparkle on off-beats in the middle section.
        for (int bar = 4; bar < 10; bar++)
        {
            int[] chordNotes = chords.First(c => c.Name == progression[bar]).Notes;
            for (int step = 0; step < 4; step++)
            {
                int midi = chordNotes[(step + 1) % 3] + 12;
                AddClip(harp, HarpNote(midi), bar * Bar + At(step + 0.5), At(0.5), 0.28f, Ms(2), Ms(90));
            }
        }

        // Drums: four-on-the-floor kick, back-beat snare, eighth hats.
        string kick = library.Path("TR808-Kick.wav");
        string snare = library.Path("TR808-Snare.wav");
        string hat = library.Path("TR808-HiHat.wav");
        string crash = library.Path("Crash-Cymbal-1.wav");
        for (int bar = 2; bar < 11; bar++)
        {
            long barStart = bar * Bar;
            AddClip(drums, kick, barStart, null, 0.9f, Ms(2), Ms(80));
            AddClip(drums, kick, barStart + At(2.5), null, 0.75f, Ms(2), Ms(80));
            AddClip(drums, snare, barStart + At(1), null, 0.62f, Ms(2), Ms(80));
            AddClip(drums, snare, barStart + At(3), null, 0.66f, Ms(2), Ms(80));
            for (int step = 0; step < 8; step++)
            {
                AddClip(drums, hat, barStart + At(step * 0.5), null,
                    step % 2 == 0 ? 0.26f : 0.15f, Ms(1), Ms(30));
            }

            if (bar == 2 || bar == 7)
            {
                AddClip(drums, crash, barStart, null, 0.35f, Ms(2), Ms(200));
            }
        }

        AddClip(drums, crash, 11 * Bar, null, 0.3f, Ms(2), Ms(600));
        AddClip(drums, kick, 11 * Bar, null, 0.8f, Ms(2), Ms(80));

        timeline.SetTempo(Bpm);

        // --- offline render ------------------------------------------------
        chains["Piano"] = Chain(
            Eq("highpass", ("Cutoff", 55f), ("Q", 0.7f)),
            Eq("eq-bell", ("Frequency", 320f), ("Gain", 1.5f), ("Q", 0.9f)),
            Eq("eq-high-shelf", ("Frequency", 6500f), ("Gain", 1.2f)),
            Effect("reverb", ("Mix", 0.16f)));
        chains["Strings"] = Chain(
            Eq("highpass", ("Cutoff", 110f), ("Q", 0.7f)),
            Effect("chorus", ("Mix", 0.25f)),
            Effect("reverb", ("Mix", 0.34f)));
        chains["Bass"] = Chain(
            Eq("highpass", ("Cutoff", 32f), ("Q", 0.7f)),
            Eq("eq-bell", ("Frequency", 110f), ("Gain", 1.8f), ("Q", 0.8f)),
            Effect("compressor", ("Threshold", -20f), ("Ratio", 3f), ("Attack", 15f), ("Release", 150f), ("Mix", 0.7f)));
        chains["Flute"] = Chain(
            Eq("highpass", ("Cutoff", 280f), ("Q", 0.7f)),
            Effect("delay", ("Mix", 0.1f), ("Feedback", 0.22f)),
            Effect("reverb", ("Mix", 0.24f)));
        chains["Drums"] = Chain(
            Effect("compressor", ("Threshold", -16f), ("Ratio", 2.5f), ("Attack", 5f), ("Release", 90f), ("Mix", 0.5f)),
            Eq("eq-high-shelf", ("Frequency", 6000f), ("Gain", 1.3f)),
            Effect("reverb", ("Mix", 0.08f)));
        chains["Harp"] = Chain(
            Eq("highpass", ("Cutoff", 500f), ("Q", 0.7f)),
            Effect("reverb", ("Mix", 0.3f)));
        var masterChain = Chain(
            Eq("highpass", ("Cutoff", 25f), ("Q", 0.7f)),
            Effect("compressor", ("Threshold", -14f), ("Ratio", 2f), ("Attack", 20f), ("Release", 180f), ("Mix", 0.35f)),
            Effect("limiter", ("Ceiling", -0.6f), ("Release", 80f), ("Mix", 1f)));

        long contentLength = timeline.TotalLengthSamples() + (long)(SampleRate * 2.5);
        var master = new float[contentLength * Channels];

        foreach (var track in timeline.Project.Tracks)
        {
            var trackBuffer = new float[contentLength * Channels];
            MixTrack(track, trackBuffer, contentLength);
            ApplyChain(chains.TryGetValue(track.Name, out var chain) ? chain : new List<PresetProcessor>(), trackBuffer, contentLength);
            for (int i = 0; i < master.Length; i++)
            {
                master[i] += trackBuffer[i];
            }
        }

        ApplyChain(masterChain, master, contentLength);
        Normalize(master, 0.89f);
        ApplyFade(master, contentLength, Ms(8), Ms(1800));

        string audioPath = Path.Combine(outputDirectory, "Ruse Song.wav");
        using (var writer = new WaveFileWriter(audioPath, new WaveFormat(SampleRate, 24, Channels)))
        {
            writer.WriteSamples(master, 0, master.Length);
        }

        // --- save a loadable preset + raw timeline project -----------------
        var preset = BuildPreset(timeline, sourceIds, tracks, chains, masterChain);
        string presetPath = Path.Combine(outputDirectory, "Ruse Song.ruse.json");
        File.WriteAllText(presetPath, preset.ToJson());

        var project = timeline.SnapshotProject(includePeaks: false);
        string projectPath = Path.Combine(outputDirectory, "Ruse Song.timeline.json");
        File.WriteAllText(projectPath, JsonSerializer.Serialize(project, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        double seconds = contentLength / (double)SampleRate;
        string summary = $"{Path.GetFileName(audioPath)} ({seconds:0.0}s, 48 kHz/24-bit), " +
                         $"{timeline.Project.Tracks.Count} tracks, {timeline.Project.Tracks.Sum(t => t.Clips.Count)} clips, " +
                         $"saved preset and timeline project.";
        AppLog.Info("Song builder: " + summary);
        return new Result(audioPath, presetPath, projectPath, summary);
    }

    private static void MixTrack(TimelineTrack track, float[] destination, long length)
    {
        if (track.Muted || track.Gain <= 0)
        {
            return;
        }

        foreach (var clip in track.Clips)
        {
            var audio = clip.Audio;
            if (audio == null || clip.LengthSamples <= 0)
            {
                continue;
            }

            int audioFrames = audio.Length / Channels;
            for (long local = 0; local < clip.LengthSamples; local++)
            {
                long outFrame = clip.StartSample + local;
                if (outFrame < 0 || outFrame >= length)
                {
                    continue;
                }

                long sourceFrame = clip.SourceOffsetSamples + local;
                if (sourceFrame < 0 || sourceFrame >= audioFrames)
                {
                    break;
                }

                float gain = clip.Gain * track.Gain;
                if (clip.FadeInSamples > 0 && local < clip.FadeInSamples)
                {
                    gain *= (float)local / clip.FadeInSamples;
                }

                long fadeOutStart = clip.LengthSamples - clip.FadeOutSamples;
                if (clip.FadeOutSamples > 0 && local >= fadeOutStart)
                {
                    gain *= (float)(clip.LengthSamples - local) / clip.FadeOutSamples;
                }

                int source = (int)sourceFrame * Channels;
                int target = (int)outFrame * Channels;
                destination[target] += audio[source] * gain;
                destination[target + 1] += audio[source + 1] * gain;
            }
        }
    }

    private static void ApplyChain(List<PresetProcessor> specs, float[] buffer, long frames)
    {
        foreach (var spec in specs)
        {
            var processor = PresetSerializer.CreateProcessor(spec);
            if (processor == null)
            {
                continue;
            }

            try
            {
                const int blockFrames = 2048;
                for (long position = 0; position < frames; position += blockFrames)
                {
                    int count = (int)Math.Min(blockFrames, frames - position);
                    processor.Process(buffer.AsSpan((int)position * Channels, count * Channels), count);
                }
            }
            finally
            {
                (processor as IDisposable)?.Dispose();
            }
        }
    }

    private static void Normalize(float[] buffer, float peakTarget)
    {
        float peak = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (float.IsFinite(buffer[i]))
            {
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            }
        }

        if (!float.IsFinite(peak) || peak <= 1e-6f)
        {
            return;
        }

        float gain = peakTarget / peak;
        if (gain < 1f || peak < peakTarget * 0.7f)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] *= gain;
            }
        }
    }

    private static void ApplyFade(float[] buffer, long frames, int fadeIn, int fadeOut)
    {
        for (int i = 0; i < fadeIn && i < frames; i++)
        {
            float gain = (float)i / fadeIn;
            buffer[i * Channels] *= gain;
            buffer[(i * Channels) + 1] *= gain;
        }

        for (int i = 0; i < fadeOut && i < frames; i++)
        {
            int frame = (int)(frames - 1 - i);
            float gain = (float)i / fadeOut;
            buffer[frame * Channels] *= gain;
            buffer[(frame * Channels) + 1] *= gain;
        }
    }

    private static List<PresetProcessor> Chain(params PresetProcessor[] processors) => processors.ToList();

    private static PresetProcessor Eq(string typeId, params (string Name, float Value)[] values) =>
        new() { TypeId = typeId, Parameters = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal) };

    private static PresetProcessor Effect(string typeId, params (string Name, float Value)[] values) =>
        new() { TypeId = typeId, Parameters = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal) };

    private static Preset BuildPreset(
        TimelineService timeline,
        IReadOnlyDictionary<string, string> sourceIds,
        IReadOnlyDictionary<string, TimelineTrack> tracks,
        IReadOnlyDictionary<string, List<PresetProcessor>> chains,
        List<PresetProcessor> masterChain)
    {
        var preset = new Preset { Name = "Ruse Song", Timeline = timeline.SnapshotProject(includePeaks: false) };
        var trackOrder = tracks.Keys.ToList();

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        preset.Outputs.Add(new PresetOutput
        {
            DeviceId = device.ID,
            Title = device.FriendlyName,
            X = 1840,
            Y = 700,
            Volume = 1f,
        });

        int outputIndex = 0;
        int masterIndex = trackOrder.Count;

        for (int i = 0; i < trackOrder.Count; i++)
        {
            string name = trackOrder[i];
            int row = i % 3;
            int columnX = i < 3 ? 60 : 760;
            int rowY = 80 + row * 620;

            preset.Sources.Add(new PresetSource
            {
                Id = sourceIds[name],
                Title = name,
                Kind = "Timeline",
                X = columnX,
                Y = rowY,
                Volume = 1f,
            });

            preset.Modifiers.Add(new PresetModifier
            {
                Title = name,
                X = columnX + 340,
                Y = rowY,
                Processors = chains.TryGetValue(name, out var chain) ? chain : new List<PresetProcessor>(),
            });

            preset.SourceModifiers.Add(new PresetSourceModifierLink
            {
                SourceIndex = i,
                ModifierIndex = i,
            });

            preset.ModifierLinks.Add(new PresetModifierLink
            {
                FromModifierIndex = i,
                ToModifierIndex = masterIndex,
            });
        }

        preset.Modifiers.Add(new PresetModifier
        {
            Title = "Master",
            X = 1500,
            Y = 700,
            Processors = masterChain,
        });

        preset.ModifierOutputs.Add(new PresetModifierOutputLink
        {
            ModifierIndex = masterIndex,
            OutputIndex = outputIndex,
            Gain = 1f,
            Enabled = true,
        });

        return preset;
    }

    private sealed class SampleLibrary
    {
        private readonly string _sampleDirectory;
        private readonly string _outputDirectory;
        private readonly Dictionary<string, float[]> _sources = new(StringComparer.OrdinalIgnoreCase);

        public SampleLibrary(string sampleDirectory, string outputDirectory)
        {
            _sampleDirectory = sampleDirectory;
            _outputDirectory = outputDirectory;
        }

        public string Path(string name) => System.IO.Path.Combine(_sampleDirectory, name);

        public string RenderChord(string name, string sample, int rootMidi, int[] notes,
            long lengthSamples, bool loop, float noteGain, int attackMs, int releaseMs)
        {
            var source = Load(sample);
            int frames = (int)lengthSamples;
            var buffer = new float[frames * Channels];
            for (int i = 0; i < notes.Length; i++)
            {
                var note = Pitch(source, notes[i] - rootMidi, 0);
                float pan = (i - (notes.Length - 1) / 2f) * 0.22f;
                Mix(note, buffer, loop, noteGain, pan);
            }

            ApplyEnvelope(buffer, frames, attackMs, releaseMs);
            Normalize(buffer, 0.8f);
            return Write(name, buffer);
        }

        public string RenderNote(string name, string sample, int rootMidi, int midi,
            long lengthSamples, bool loop, float noteGain, int attackMs, int releaseMs)
        {
            var source = Load(sample);
            int frames = (int)lengthSamples;
            var buffer = new float[frames * Channels];
            var note = Pitch(source, midi - rootMidi, 0);
            Mix(note, buffer, loop, noteGain, 0);
            ApplyEnvelope(buffer, frames, attackMs, releaseMs);
            Normalize(buffer, 0.8f);
            return Write(name, buffer);
        }

        private float[] Load(string name)
        {
            if (_sources.TryGetValue(name, out var cached))
            {
                return cached;
            }

            string path = Path(name);
            using var reader = new AudioFileReader(path);
            var converter = new FormatConverter(reader.WaveFormat.Channels, reader.WaveFormat.SampleRate);
            var output = new List<float>();
            var bytes = new byte[Math.Max(4, reader.WaveFormat.AverageBytesPerSecond / 4)];
            var block = new float[bytes.Length / sizeof(float)];
            int read;
            while ((read = reader.Read(bytes, 0, bytes.Length)) > 0)
            {
                int floats = read / sizeof(float);
                Buffer.BlockCopy(bytes, 0, block, 0, floats * sizeof(float));
                converter.Convert(block.AsSpan(0, floats), output);
            }

            var samples = output.ToArray();
            _sources[name] = samples;
            return samples;
        }

        private static float[] Pitch(float[] source, double semitones, float cents)
        {
            double ratio = Math.Pow(2.0, (semitones + cents / 100.0) / 12.0);
            int sourceFrames = source.Length / Channels;
            int targetFrames = Math.Max(1, (int)Math.Floor(sourceFrames / ratio));
            var target = new float[targetFrames * Channels];
            for (int frame = 0; frame < targetFrames; frame++)
            {
                double position = frame * ratio;
                int index = Math.Min(sourceFrames - 1, (int)position);
                int next = Math.Min(sourceFrames - 1, index + 1);
                float blend = (float)(position - index);
                int from = index * Channels;
                int to = next * Channels;
                int dst = frame * Channels;
                target[dst] = source[from] * (1f - blend) + source[to] * blend;
                target[dst + 1] = source[from + 1] * (1f - blend) + source[to + 1] * blend;
            }

            return target;
        }

        private static void Mix(float[] source, float[] destination, bool loop, float gain, float pan)
        {
            int sourceFrames = source.Length / Channels;
            int targetFrames = destination.Length / Channels;
            int fade = Math.Min(2048, Math.Max(1, sourceFrames / 4));
            int period = Math.Max(1, sourceFrames - fade);
            double angle = (Math.Clamp(pan, -1f, 1f) + 1.0) * Math.PI / 4.0;
            float left = (float)Math.Cos(angle) * 1.41421356f;
            float right = (float)Math.Sin(angle) * 1.41421356f;
            for (int frame = 0; frame < targetFrames; frame++)
            {
                int sourceFrame;
                float blend = 0;
                if (loop && sourceFrames > fade)
                {
                    sourceFrame = frame % period;
                    if (sourceFrame < fade)
                    {
                        blend = 1f - (float)sourceFrame / fade;
                    }
                }
                else
                {
                    sourceFrame = frame;
                }

                if (sourceFrame >= sourceFrames)
                {
                    break;
                }

                int src = sourceFrame * Channels;
                float l = source[src];
                float r = source[src + 1];
                if (blend > 0)
                {
                    int tail = (sourceFrames - fade + sourceFrame) * Channels;
                    l = l * (1f - blend) + source[tail] * blend;
                    r = r * (1f - blend) + source[tail + 1] * blend;
                }

                int dst = frame * Channels;
                destination[dst] += l * gain * left;
                destination[dst + 1] += r * gain * right;
            }
        }

        private static void ApplyEnvelope(float[] buffer, int frames, int attackMs, int releaseMs)
        {
            int attack = Math.Min(frames / 2, attackMs * SampleRate / 1000);
            int release = Math.Min(frames / 2, releaseMs * SampleRate / 1000);
            for (int i = 0; i < attack && i < frames; i++)
            {
                float gain = (float)i / attack;
                buffer[i * Channels] *= gain;
                buffer[(i * Channels) + 1] *= gain;
            }

            for (int i = 0; i < release && i < frames; i++)
            {
                int frame = frames - 1 - i;
                float gain = (float)i / release;
                buffer[frame * Channels] *= gain;
                buffer[(frame * Channels) + 1] *= gain;
            }
        }

        private static void Normalize(float[] buffer, float peakTarget)
        {
            float peak = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (float.IsFinite(buffer[i]))
                {
                    peak = Math.Max(peak, Math.Abs(buffer[i]));
                }
            }

            if (!float.IsFinite(peak) || peak <= 1e-6f)
            {
                return;
            }

            float gain = peakTarget / peak;
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] *= gain;
            }
        }

        private string Write(string name, float[] buffer)
        {
            string path = System.IO.Path.Combine(_outputDirectory, name + ".wav");
            using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 24, Channels));
            writer.WriteSamples(buffer, 0, buffer.Length);
            return path;
        }
    }
}
