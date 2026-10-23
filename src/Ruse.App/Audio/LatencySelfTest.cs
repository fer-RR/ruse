using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Measures the software latency the routing graph adds between a source and an
/// output. It bypasses physical hardware by using an in-memory sink, so the
/// result reflects only what Ruse itself contributes (ring buffer + pump).
/// </summary>
public static class LatencySelfTest
{
    public sealed record Result(
        double SourceToSinkMs,
        double PumpChunkMs,
        int SamplesDelivered,
        string Summary);

    public static Result Run(int iterations = 200)
    {
        const int sampleRate = 48000;
        const int channels = 2;
        const int chunkFrames = 240; // must match OutputNode's pump chunk

        var source = new SourceNode("selftest", SourceKind.Application);

        int framesReceived = 0;
        int signalFrame = -1;
        float signalValue = 0f;

        var output = OutputNode.CreateForTest((mix, length) =>
        {
            int frames = length / channels;
            for (int f = 0; f < frames; f++)
            {
                // Look for a non-zero frame (the impulse we injected).
                if (mix[f * channels] != 0f && signalFrame < 0)
                {
                    signalFrame = framesReceived + f;
                    signalValue = mix[f * channels];
                }
            }

            framesReceived += frames;
        });

        // Apply a modifier in the real render path: half gain. The impulse must
        // emerge at half amplitude, proving Route.Processors runs before mixing.
        var gain = new GainProcessor();
        gain.Gain = 0.5f;
        output.Inputs.Add(new OutputNode.Route
        {
            Source = source,
            Gain = 1f,
            Processors = new IAudioProcessor[] { gain },
        });
        output.RefreshRoutes();

        // Render synchronously (no pump thread) so the result is deterministic:
        // 5 chunks of leading silence put the impulse at a known frame index.
        const int leadFrames = chunkFrames * 5;
        var mix = new float[chunkFrames * channels];
        var temp = new float[chunkFrames * channels];
        var byteBuffer = new byte[mix.Length * 4];

        source.WriteTestSamples(new float[leadFrames * channels]);

        var impulse = new float[chunkFrames * channels];
        impulse[0] = 1f;
        impulse[1] = 1f;
        source.WriteTestSamples(impulse);

        int guard = 0;
        while (signalFrame < 0 && guard < iterations)
        {
            output.RenderChunk(mix, temp, byteBuffer, channels);
            guard++;
        }

        output.Dispose();
        source.Dispose();

        double chunkMs = chunkFrames * 1000.0 / sampleRate;
        int addedFrames = signalFrame - leadFrames;
        double latencyMs = signalFrame < 0 ? -1 : addedFrames * 1000.0 / sampleRate;

        string summary;
        if (signalFrame < 0)
        {
            summary = $"FAILED: impulse never reached the sink (pump chunk {chunkMs:0.0} ms).";
        }
        else if (addedFrames != 0)
        {
            summary = $"FAILED: software path added {addedFrames} frames " +
                      $"(expected 0; impulse at {leadFrames} emerged at {signalFrame}).";
        }
        else if (Math.Abs(signalValue - 0.5f) > 1e-4)
        {
            summary = $"FAILED: modifier not applied (impulse amplitude {signalValue:0.###}, want 0.5).";
        }
        else
        {
            summary = $"Software path 0.0 ms added / sample-accurate " +
                      $"(impulse at frame {leadFrames} emerged at {signalFrame}, amplitude {signalValue:0.00}) " +
                      $"| pump chunk {chunkMs:0.0} ms.";
        }

        return new Result(latencyMs, chunkMs, framesReceived, summary);
    }

    public sealed record ConverterTestResult(bool Passed, string Summary);

    /// <summary>
    /// Verifies FormatConverter: monoâ†’stereo duplication, sample-rate length
    /// (44100â†’48000) and continuity of a sine wave across block boundaries.
    /// </summary>
    public static ConverterTestResult TestConverter()
    {
        var failures = new List<string>();

        // --- mono 48k â†’ stereo 48k: each mono frame duplicated to L/R ---
        {
            var conv = new FormatConverter(1, 48000);
            var input = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
            var output = new List<float>();
            int frames = conv.Convert(input, output);

            if (frames != 4 || output.Count != 8)
            {
                failures.Add($"mono48: expected 4 frames/8 samples, got {frames}/{output.Count}");
            }
            else if (output[0] != 0.1f || output[1] != 0.1f || output[6] != 0.4f || output[7] != 0.4f)
            {
                failures.Add("mono48: duplication incorrect");
            }
        }

        // --- stereo 44.1k â†’ 48k: output length â‰ˆ input * 48000/44100 ---
        {
            var conv = new FormatConverter(2, 44100);
            int srcFrames = 4410; // 0.1s
            var input = new float[srcFrames * 2];
            // 1 kHz sine at 44.1 kHz
            for (int f = 0; f < srcFrames; f++)
            {
                float s = MathF.Sin(2 * MathF.PI * 1000 * f / 44100f);
                input[f * 2] = s;
                input[f * 2 + 1] = s;
            }

            var output = new List<float>();
            int outFrames = conv.Convert(input, output);
            int expected = (int)(srcFrames * (48000.0 / 44100.0));
            double error = Math.Abs(outFrames - expected) / (double)expected;
            if (error > 0.02)
            {
                failures.Add($"stereo44.1: expected ~{expected} frames, got {outFrames}");
            }

            // Dominant frequency should still be ~1 kHz (measure zero crossings).
            int crossings = 0;
            for (int f = 1; f < outFrames; f++)
            {
                float prev = output[(f - 1) * 2];
                float cur = output[f * 2];
                if ((prev < 0 && cur >= 0) || (prev >= 0 && cur < 0))
                {
                    crossings++;
                }
            }

            double measuredHz = crossings / 2.0 / (outFrames / 48000.0);
            if (Math.Abs(measuredHz - 1000) > 60)
            {
                failures.Add($"stereo44.1: pitch wrong, measured {measuredHz:0} Hz (want ~1000)");
            }
        }

        // --- 5.1 (6ch) â†’ stereo: first two channels kept ---
        {
            var conv = new FormatConverter(6, 48000);
            var input = new float[6 * 2];
            input[0] = 0.5f; // L
            input[1] = -0.5f; // R
            input[2] = 0.9f; // C
            input[6] = 0.25f;
            input[7] = -0.25f;
            var output = new List<float>();
            conv.Convert(input, output);
            if (output.Count != 4 || output[0] != 0.5f || output[1] != -0.5f || output[2] != 0.25f)
            {
                failures.Add("5.1â†’stereo: channel selection incorrect");
            }
        }

        // --- stereo 96k â†’ 48k: half the frames, same pitch ---
        {
            var conv = new FormatConverter(2, 96000);
            int srcFrames = 9600; // 0.1s
            var input = new float[srcFrames * 2];
            for (int f = 0; f < srcFrames; f++)
            {
                float s = MathF.Sin(2 * MathF.PI * 1000 * f / 96000f);
                input[f * 2] = s;
                input[f * 2 + 1] = s;
            }

            var output = new List<float>();
            int outFrames = conv.Convert(input, output);
            int expected = srcFrames / 2;
            double error = Math.Abs(outFrames - expected) / (double)expected;
            if (error > 0.02)
            {
                failures.Add($"stereo96: expected ~{expected} frames, got {outFrames}");
            }

            int crossings = 0;
            for (int f = 1; f < outFrames; f++)
            {
                float prev = output[(f - 1) * 2];
                float cur = output[f * 2];
                if ((prev < 0 && cur >= 0) || (prev >= 0 && cur < 0))
                {
                    crossings++;
                }
            }

            double measuredHz = crossings / 2.0 / (outFrames / 48000.0);
            if (Math.Abs(measuredHz - 1000) > 60)
            {
                failures.Add($"stereo96: pitch wrong, measured {measuredHz:0} Hz (want ~1000)");
            }
        }

        // --- continuity across block boundaries (no clicks at 1000 Hz) ---
        {
            var conv = new FormatConverter(2, 44100);
            var output = new List<float>();
            int totalSrcFrames = 44100; // 1 second
            const int blockFrames = 441; // 10 blocks
            for (int b = 0; b < totalSrcFrames / blockFrames; b++)
            {
                var block = new float[blockFrames * 2];
                for (int f = 0; f < blockFrames; f++)
                {
                    int global = b * blockFrames + f;
                    float s = MathF.Sin(2 * MathF.PI * 1000 * global / 44100f);
                    block[f * 2] = s;
                    block[f * 2 + 1] = s;
                }

                conv.Convert(block, output);
            }

            // Largest sample-to-sample jump should be small for a 1 kHz sine.
            float maxJump = 0;
            for (int f = 1; f < output.Count / 2; f++)
            {
                float jump = Math.Abs(output[f * 2] - output[(f - 1) * 2]);
                if (jump > maxJump)
                {
                    maxJump = jump;
                }
            }

            // At 1 kHz/48 kHz, per-sample delta of a sine is ~0.13 max.
            if (maxJump > 0.25f)
            {
                failures.Add($"continuity: discontinuity across boundary, max jump {maxJump:0.000}");
            }
        }

        bool passed = failures.Count == 0;
        string summary = passed
            ? "FormatConverter: all checks passed (monoâ†’stereo, 44.1/96â†’48 kHz, 5.1â†’stereo, block continuity)."
            : "FormatConverter FAILED: " + string.Join("; ", failures);
        return new ConverterTestResult(passed, summary);
    }

    public sealed record ModifierTestResult(bool Passed, string Summary);

    /// <summary>
    /// Verifies the modifier pipeline end-to-end at the graph level: a source
    /// routed through one or more modifiers must expose those processors, in
    /// signal order, on the output's route â€” and removing a modifier must
    /// clear them. Pure data-flow; no devices or threads involved.
    /// </summary>
    public static ModifierTestResult TestModifier()
    {
        var failures = new List<string>();
        var graph = new AudioGraph
        {
            // Keep the test purely data-flow: no capture/render threads.
            AutoRun = false,
        };

        var source = new SourceNode("mod-src", SourceKind.Application);
        graph.AddSource(source);

        var output = graph.AddOutputForTest((_, _) => { });

        var gainA = new GainProcessor();
        gainA.Gain = 0.5f;
        var modA = new ModifierNode("A");
        modA.Add(gainA);

        var gainB = new GainProcessor();
        gainB.Gain = 2.0f;
        var modB = new ModifierNode("B");
        modB.Add(gainB);

        graph.AddModifier(modA);
        graph.AddModifier(modB);

        // source â†’ A â†’ B â†’ output, and also A â†’ output directly, so removing B
        // should leave A's effect in place rather than clearing the chain.
        graph.ConnectSourceToModifier(source, modA);
        graph.ConnectModifierToModifier(modA, modB);
        graph.ConnectModifierToOutput(modB, output);
        graph.ConnectModifierToOutput(modA, output);

        if (output.Inputs.Count != 1)
        {
            failures.Add($"expected 1 route on output, got {output.Inputs.Count}");
        }

        var route = output.Inputs.FirstOrDefault();
        if (route == null)
        {
            failures.Add("output has no route");
        }
        else
        {
            if (route.Processors.Length != 2)
            {
                failures.Add($"expected 2 processors in chain, got {route.Processors.Length}");
            }
            else if (route.Processors[0].TypeId != GainProcessor.Id || route.Processors[1].TypeId != GainProcessor.Id)
            {
                failures.Add("chain order is not A then B");
            }
            else if (ReferenceEquals(route.Processors[0], gainA) || ReferenceEquals(route.Processors[1], gainB))
            {
                failures.Add("route reuses the modifier's processor instance instead of a private one");
            }

            // The chain must actually scale the signal: 0.5 * 2.0 = 1.0 (unity).
            var block = new[] { 1f, -1f, 0.25f, -0.25f };
            foreach (var processor in route.Processors)
            {
                processor.Process(block, block.Length / 2);
            }

            if (Math.Abs(block[0] - 1f) > 1e-5 || Math.Abs(block[1] + 1f) > 1e-5)
            {
                failures.Add($"chain math wrong: got {block[0]:0.###}, {block[1]:0.###} (want 1, -1)");
            }

            // Editing the modifier's parameters must reach the route's own
            // instances: 0.25 * 2.0 = 0.5 through the real render path.
            gainA.Gain = 0.25f;
            var mirrored = new[] { 1f, 1f };
            route.Process(mirrored, 1, 2);
            if (Math.Abs(mirrored[0] - 0.5f) > 1e-5)
            {
                failures.Add($"parameter edit did not mirror to the route (got {mirrored[0]:0.###}, want 0.5)");
            }
        }

        // Removing the downstream modifier must drop it while keeping A's effect.
        graph.RemoveModifier(modB);
        if (output.Inputs.Count == 0)
        {
            failures.Add("output lost its route after removing B");
        }
        else if (output.Inputs[0].Processors.Length != 1 ||
                 output.Inputs[0].Processors[0].TypeId != GainProcessor.Id)
        {
            failures.Add($"expected [A] after removing B, got {output.Inputs[0].Processors.Length} processor(s)");
        }

        graph.Dispose();

        bool passed = failures.Count == 0;
        string summary = passed
            ? "Modifier pipeline: sourceâ†’Aâ†’Bâ†’output chained 2 processors in order, gain math correct, removing B left A intact."
            : "Modifier pipeline FAILED: " + string.Join("; ", failures);
        return new ModifierTestResult(passed, summary);
    }

    /// <summary>
    /// Test-only effect that genuinely delays by <see cref="DelayFrames"/> and
    /// reports it as latency, standing in for a look-ahead effect.
    /// </summary>
    private sealed class TestDelayProcessor : IAudioProcessor
    {
        private readonly float[] _buffer;
        private int _write;

        public TestDelayProcessor(int delayFrames)
        {
            DelayFrames = delayFrames;
            _buffer = new float[Math.Max(1, delayFrames * 2)];
        }

        public int DelayFrames { get; }
        public string TypeId => "test-delay";
        public string DisplayName => "Test delay";
        public int LatencySamples => DelayFrames;

        public void Process(Span<float> samples, int frames)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float delayed = _buffer[_write];
                _buffer[_write] = samples[i];
                samples[i] = delayed;
                _write++;
                if (_write >= _buffer.Length)
                {
                    _write = 0;
                }
            }
        }

        public void Reset()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _write = 0;
        }
    }

    public sealed record AlignmentTestResult(bool Passed, string Summary);

    /// <summary>
    /// Verifies route alignment: each route gets its own effect instances, and a
    /// route with an inherently delayed effect pulls the other routes onto the
    /// same time base so parallel audio sums in phase instead of blurring.
    /// </summary>
    public static AlignmentTestResult TestLatencyAlignment()
    {
        const int channels = 2;
        const int chunkFrames = 240;
        const int delayFrames = 240; // 5 ms of look-ahead
        const int leadFrames = chunkFrames * 2;

        var failures = new List<string>();
        var graph = new AudioGraph { AutoRun = false };

        var delayedSource = new SourceNode("delayed", SourceKind.Application);
        var cleanSource = new SourceNode("clean", SourceKind.Application);
        var gainSourceA = new SourceNode("gain-a", SourceKind.Application);
        var gainSourceB = new SourceNode("gain-b", SourceKind.Application);
        graph.AddSource(delayedSource);
        graph.AddSource(cleanSource);
        graph.AddSource(gainSourceA);
        graph.AddSource(gainSourceB);

        int signalFrame = -1;
        float signalValue = 0f;
        int framesReceived = 0;
        double peak = 0;
        var output = graph.AddOutputForTest((mix, length) =>
        {
            int frames = length / channels;
            for (int f = 0; f < frames; f++)
            {
                if (Math.Abs(mix[f * channels]) > peak)
                {
                    peak = Math.Abs(mix[f * channels]);
                }

                if (mix[f * channels] != 0f && signalFrame < 0)
                {
                    signalFrame = framesReceived + f;
                    signalValue = mix[f * channels];
                }
            }

            framesReceived += frames;
        });

        // A route whose effect genuinely delays by `delayFrames`.
        var delayModifier = new ModifierNode("Delay");
        delayModifier.Add(new TestDelayProcessor(delayFrames));
        graph.AddModifier(delayModifier);
        graph.ConnectSourceToModifier(delayedSource, delayModifier);
        graph.ConnectModifierToOutput(delayModifier, output);
        graph.Connect(cleanSource, output);

        // A modifier shared by two sources: each route must get its own instance.
        var gain = new GainProcessor();
        gain.Gain = 0.5f;
        var gainModifier = new ModifierNode("Shared gain");
        gainModifier.Add(gain);
        graph.AddModifier(gainModifier);
        graph.ConnectSourceToModifier(gainSourceA, gainModifier);
        graph.ConnectSourceToModifier(gainSourceB, gainModifier);
        graph.ConnectModifierToOutput(gainModifier, output);

        var delayedRoute = output.Inputs.FirstOrDefault(r => r.Source == delayedSource);
        var cleanRoute = output.Inputs.FirstOrDefault(r => r.Source == cleanSource);
        var gainRouteA = output.Inputs.FirstOrDefault(r => r.Source == gainSourceA);
        var gainRouteB = output.Inputs.FirstOrDefault(r => r.Source == gainSourceB);

        if (delayedRoute == null || cleanRoute == null || gainRouteA == null || gainRouteB == null)
        {
            failures.Add("expected 4 routes, missing one");
        }
        else
        {
            if (delayedRoute.InherentLatencyFrames != delayFrames || delayedRoute.CompensationFrames != 0)
            {
                failures.Add($"delayed route: inherent {delayedRoute.InherentLatencyFrames}, " +
                             $"compensation {delayedRoute.CompensationFrames} (want {delayFrames}/0)");
            }

            if (cleanRoute.InherentLatencyFrames != 0 || cleanRoute.CompensationFrames != delayFrames)
            {
                failures.Add($"clean route: inherent {cleanRoute.InherentLatencyFrames}, " +
                             $"compensation {cleanRoute.CompensationFrames} (want 0/{delayFrames})");
            }

            if (gainRouteA.Processors.Length != 1 || gainRouteB.Processors.Length != 1)
            {
                failures.Add("shared modifier did not produce one processor per route");
            }
            else if (ReferenceEquals(gainRouteA.Processors[0], gainRouteB.Processors[0]) ||
                     ReferenceEquals(gainRouteA.Processors[0], gain))
            {
                failures.Add("routes fed by one modifier share a processor instance");
            }
            else if (gainRouteA.Processors[0] is IParameterizedProcessor parameters &&
                     Math.Abs(parameters.Parameters.First(p => p.Name == "Gain").Get() - 0.5f) > 1e-5)
            {
                failures.Add("per-route instance did not inherit the modifier's parameters");
            }
        }

        // Render: both impulses must land on the same frame once aligned.
        var mix = new float[chunkFrames * channels];
        var temp = new float[chunkFrames * channels];
        var byteBuffer = new byte[mix.Length * 4];

        var silence = new float[leadFrames * channels];
        delayedSource.WriteTestSamples(silence);
        cleanSource.WriteTestSamples(silence);

        var impulse = new float[chunkFrames * channels];
        impulse[0] = 0.4f;
        impulse[1] = 0.4f;
        delayedSource.WriteTestSamples(impulse);
        cleanSource.WriteTestSamples(impulse);

        int expectedFrame = leadFrames + delayFrames;
        int guard = 0;
        while (signalFrame < 0 && guard < 60)
        {
            output.RenderChunk(mix, temp, byteBuffer, channels);
            guard++;
        }

        if (signalFrame < 0)
        {
            failures.Add($"aligned impulse never reached the sink " +
                         $"(rendered {framesReceived} frames, peak {peak:0.####})");
        }
        else if (signalFrame != expectedFrame)
        {
            failures.Add($"aligned impulse at frame {signalFrame}, want {expectedFrame} " +
                         "(routes were not time-aligned)");
        }
        else if (Math.Abs(signalValue - 0.8f) > 1e-3)
        {
            failures.Add($"aligned impulse amplitude {signalValue:0.###}, want 0.8 (both routes in phase)");
        }

        graph.Dispose();

        bool passed = failures.Count == 0;
        string summary = passed
            ? $"Route alignment: 240-frame look-ahead route pulled a clean route onto the same frame " +
              $"(both impulses coincided at frame {expectedFrame}, amplitude 0.80); shares no effect instances."
            : "Route alignment FAILED: " + string.Join("; ", failures);
        return new AlignmentTestResult(passed, summary);
    }

    public sealed record PresetTestResult(bool Passed, string Summary);

    /// <summary>
    /// Round-trips a preset through JSON and then rebuilds its modifier chain
    /// in the same order <c>ApplyPreset</c> does. This covers the part of
    /// persistence that is easy to break silently: link indices, effect order
    /// and parameter values.
    /// </summary>
    public static PresetTestResult TestPresetRoundTrip()
    {
        var failures = new List<string>();

        var preset = new Preset { Name = "round-trip" };
        preset.Sources.Add(new PresetSource
        {
            Title = "Mic",
            Kind = "Device",
            DeviceId = "dev-1",
            X = 12,
            Y = 34,
            Volume = 0.8f,
            Muted = true,
        });
        preset.Outputs.Add(new PresetOutput
        {
            DeviceId = "out-1",
            Title = "Virtual Mic",
            IsVirtual = true,
            MicHint = "Voicemeeter Out B1",
            X = 520,
            Y = 60,
            Volume = 0.9f,
        });
        preset.Outputs.Add(new PresetOutput
        {
            DeviceId = "file:take-1",
            Title = "take.flac",
            IsFile = true,
            FilePath = @"C:\Music\take.flac",
            FileFormat = "Flac",
            X = 700,
            Y = 80,
        });

        preset.Modifiers.Add(new PresetModifier
        {
            Title = "Gain",
            X = 200,
            Y = 120,
            Processors =
            {
                new PresetProcessor
                {
                    TypeId = GainProcessor.Id,
                    Parameters = { ["Gain"] = 0.5f },
                },
            },
        });
        preset.Modifiers.Add(new PresetModifier
        {
            Title = "High-pass",
            X = 340,
            Y = 120,
            Processors =
            {
                new PresetProcessor
                {
                    TypeId = HighPassProcessor.Id,
                    Parameters = { ["Cutoff"] = 120f },
                },
            },
        });
        preset.Modifiers.Add(new PresetModifier
        {
            Title = "Unknown",
            X = 400,
            Y = 200,
            Processors = { new PresetProcessor { TypeId = "bogus-effect" } },
        });

        preset.SourceModifiers.Add(new PresetSourceModifierLink { SourceIndex = 0, ModifierIndex = 0 });
        preset.ModifierLinks.Add(new PresetModifierLink { FromModifierIndex = 0, ToModifierIndex = 1 });
        preset.ModifierOutputs.Add(new PresetModifierOutputLink { ModifierIndex = 1, OutputIndex = 0 });

        string json = preset.ToJson();
        Preset back;
        try
        {
            back = Preset.FromJson(json);
        }
        catch (Exception ex)
        {
            return new PresetTestResult(false, $"Preset round-trip FAILED: FromJson threw {ex.Message}");
        }

        if (back.Name != "round-trip" ||
            back.Sources.Count != 1 ||
            back.Outputs.Count != 2 ||
            back.Modifiers.Count != 3 ||
            back.SourceModifiers.Count != 1 ||
            back.ModifierLinks.Count != 1 ||
            back.ModifierOutputs.Count != 1)
        {
            failures.Add("JSON lost sources/outputs/modifiers/links");
        }
        else
        {
            if (back.Sources[0].Kind != "Device" || back.Sources[0].DeviceId != "dev-1" ||
                Math.Abs(back.Sources[0].Volume - 0.8f) > 1e-6 || !back.Sources[0].Muted)
            {
                failures.Add("source fields changed across save/load");
            }

            if (back.Outputs[0].MicHint != "Voicemeeter Out B1" || !back.Outputs[0].IsVirtual)
            {
                failures.Add("output fields changed across save/load");
            }

            if (!back.Outputs[1].IsFile ||
                back.Outputs[1].FilePath != @"C:\Music\take.flac" ||
                back.Outputs[1].FileFormat != "Flac")
            {
                failures.Add("file recorder fields changed across save/load");
            }

            if (back.Modifiers[1].Processors.Count != 1 ||
                back.Modifiers[1].Processors[0].TypeId != HighPassProcessor.Id ||
                !back.Modifiers[1].Processors[0].Parameters.TryGetValue("Cutoff", out float cutoff) ||
                Math.Abs(cutoff - 120f) > 1e-6)
            {
                failures.Add("processor type or parameter changed across save/load");
            }
        }

        if (back.ToJson() != json)
        {
            failures.Add("JSON is not stable across a save/load cycle");
        }

        // Replay the apply order used by ApplyPreset: nodes first, then links.
        var graph = new AudioGraph { AutoRun = false };
        var source = new SourceNode("Mic", SourceKind.Device, null, "dev-1");
        graph.AddSource(source);
        var output = graph.AddOutputForTest((_, _) => { });

        var modifiers = new ModifierNode?[back.Modifiers.Count];
        for (int i = 0; i < back.Modifiers.Count; i++)
        {
            var saved = back.Modifiers[i];
            var modifier = new ModifierNode(saved.Title);
            foreach (var entry in saved.Processors)
            {
                var processor = PresetSerializer.CreateProcessor(entry);
                if (processor != null)
                {
                    modifier.Add(processor);
                }
            }

            if (modifier.Count == 0)
            {
                var fallback = AudioProcessorCatalog.Create(GainProcessor.Id);
                if (fallback != null)
                {
                    modifier.Add(fallback);
                }
            }

            graph.AddModifier(modifier);
            modifiers[i] = modifier;
        }

        foreach (var link in back.SourceModifiers)
        {
            if (TryIndex(modifiers, link.ModifierIndex, out var modifier))
            {
                graph.ConnectSourceToModifier(source, modifier);
            }
            else
            {
                failures.Add("sourceâ†’modifier link index out of range");
            }
        }

        foreach (var link in back.ModifierLinks)
        {
            if (TryIndex(modifiers, link.FromModifierIndex, out var from) &&
                TryIndex(modifiers, link.ToModifierIndex, out var to))
            {
                graph.ConnectModifierToModifier(from, to);
            }
            else
            {
                failures.Add("modifierâ†’modifier link index out of range");
            }
        }

        foreach (var link in back.ModifierOutputs)
        {
            if (TryIndex(modifiers, link.ModifierIndex, out var modifier))
            {
                graph.ConnectModifierToOutput(modifier, output);
            }
            else
            {
                failures.Add("modifierâ†’output link index out of range");
            }
        }

        if (output.Inputs.Count != 1)
        {
            failures.Add($"expected 1 route after replay, got {output.Inputs.Count}");
        }
        else
        {
            var route = output.Inputs[0];
            if (route.Processors.Length != 2 ||
                route.Processors[0].TypeId != GainProcessor.Id ||
                route.Processors[1].TypeId != HighPassProcessor.Id)
            {
                failures.Add($"replayed chain is not [Gain, High-pass] (got {route.Processors.Length} effect(s))");
            }
            else
            {
                float gainValue = ((IParameterizedProcessor)route.Processors[0]).Parameters
                    .Single(p => p.Name == "Gain").Get();
                float cutoffValue = ((IParameterizedProcessor)route.Processors[1]).Parameters
                    .Single(p => p.Name == "Cutoff").Get();

                if (Math.Abs(gainValue - 0.5f) > 1e-6)
                {
                    failures.Add($"replayed gain is {gainValue:0.##}, want 0.5");
                }

                if (Math.Abs(cutoffValue - 120f) > 1e-6)
                {
                    failures.Add($"replayed cutoff is {cutoffValue:0.##}, want 120");
                }
            }
        }

        if (PresetSerializer.CreateProcessor(new PresetProcessor { TypeId = "bogus-effect" }) != null)
        {
            failures.Add("unknown processor type should not resolve");
        }

        graph.Dispose();

        bool passed = failures.Count == 0;
        string summary = passed
            ? "Preset round-trip: 3 modifiers, 3 links and all parameters survived JSON; replayed chain [Gain 0.50 â†’ High-pass 120 Hz]; unknown effect skipped."
            : "Preset round-trip FAILED: " + string.Join("; ", failures);
        return new PresetTestResult(passed, summary);
    }

    private static bool TryIndex(ModifierNode?[] list, int index, out ModifierNode modifier)
    {
        modifier = null!;
        if (index < 0 || index >= list.Length || list[index] == null)
        {
            return false;
        }

        modifier = list[index]!;
        return true;
    }

    public sealed record FilterTestResult(bool Passed, string Summary);

    /// <summary>
    /// Checks the high-pass filter's actual response: stop-band rejection,
    /// pass-band transparency, state continuity across block boundaries and DC
    /// removal. Everything runs through the same Process path as the mixer.
    /// </summary>
    public static FilterTestResult TestFilters()
    {
        const int sampleRate = 48000;
        const int frames = 9600; // 200 ms
        var failures = new List<string>();

        float Rms(float[] data, int startFrame, int frameCount)
        {
            double sum = 0;
            for (int f = startFrame; f < startFrame + frameCount; f++)
            {
                float l = data[f * 2];
                float r = data[f * 2 + 1];
                sum += l * l + r * r;
            }

            return (float)Math.Sqrt(sum / (frameCount * 2));
        }

        float[] MakeSine(float hz, float amplitude)
        {
            var data = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float s = amplitude * MathF.Sin(2 * MathF.PI * hz * f / sampleRate);
                data[f * 2] = s;
                data[f * 2 + 1] = s;
            }

            return data;
        }

        void ProcessChunks(IAudioProcessor processor, float[] data)
        {
            const int chunk = 240;
            for (int f = 0; f < frames; f += chunk)
            {
                int n = Math.Min(chunk, frames - f);
                processor.Process(data.AsSpan(f * 2, n * 2), n);
            }
        }

        // --- stop band: 100 Hz tone through a 1 kHz high-pass ---
        {
            var hp = new HighPassProcessor { Cutoff = 1000f };
            var data = MakeSine(100f, 1f);
            ProcessChunks(hp, data);
            float rms = Rms(data, frames / 2, frames / 2); // ignore startup transient
            if (rms > 0.05f)
            {
                failures.Add($"100 Hz leaked through 1 kHz high-pass (RMS {rms:0.###})");
            }
        }

        // --- pass band: 5 kHz tone through a 100 Hz high-pass ---
        {
            var hp = new HighPassProcessor { Cutoff = 100f };
            var data = MakeSine(5000f, 1f);
            ProcessChunks(hp, data);
            float rms = Rms(data, frames / 2, frames / 2);
            if (rms < 0.65f)
            {
                failures.Add($"5 kHz was attenuated by 100 Hz high-pass (RMS {rms:0.###})");
            }
        }

        // --- DC removal and Reset ---
        {
            var hp = new HighPassProcessor { Cutoff = 80f };
            var data = new float[frames * 2];
            Array.Fill(data, 0.5f);
            ProcessChunks(hp, data);
            float tail = Rms(data, frames - 2400, 2400);
            if (tail > 1e-3f)
            {
                failures.Add($"DC was not removed (tail RMS {tail:0.####})");
            }

            hp.Reset();
            var silence = new float[480 * 2];
            hp.Process(silence, 480);
            if (Rms(silence, 0, 480) > 1e-6f)
            {
                failures.Add("Reset did not clear filter state");
            }
        }

        bool passed = failures.Count == 0;
        string summary = passed
            ? "High-pass: 100 Hz rejected by 1 kHz cutoff, 5 kHz passed by 100 Hz cutoff, state continuous across 240-frame blocks, DC removed, Reset clean."
            : "High-pass FAILED: " + string.Join("; ", failures);
        return new FilterTestResult(passed, summary);
    }

    public sealed record EffectsTestResult(bool Passed, string Summary);

    /// <summary>
    /// Exercises every registered modifier processor (including the NAudio.Effects
    /// suite): parameter round-trip, finite output while processing real audio,
    /// and a preset save/restore round-trip. Also reports per-effect CPU cost.
    /// </summary>
    public static EffectsTestResult TestEffects()
    {
        const int sampleRate = 48000;
        const int channels = 2;
        const int frames = 240; // matches the mixer pump
        const int blocks = sampleRate / frames * 5; // 5 seconds of audio

        var failures = new List<string>();
        var timings = new List<(string Name, double Ms)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in AudioProcessorCatalog.All)
        {
            if (!seenIds.Add(descriptor.TypeId))
            {
                failures.Add($"duplicate type id '{descriptor.TypeId}'");
            }

            var processor = descriptor.Create();
            if (processor.TypeId != descriptor.TypeId)
            {
                failures.Add($"{descriptor.DisplayName}: TypeId mismatch");
            }

            if (processor is not IParameterizedProcessor parameterized)
            {
                continue;
            }

            // --- parameter round-trip ---
            foreach (var parameter in parameterized.Parameters)
            {
                if (parameter.IsToggle)
                {
                    parameter.Set(1f);
                    if (parameter.Get() < 0.5f)
                    {
                        failures.Add($"{descriptor.DisplayName}/{parameter.Name}: toggle did not latch on");
                    }

                    parameter.Set(0f);
                    if (parameter.Get() >= 0.5f)
                    {
                        failures.Add($"{descriptor.DisplayName}/{parameter.Name}: toggle did not latch off");
                    }

                    continue;
                }

                if (parameter.Max <= parameter.Min)
                {
                    continue;
                }

                float mid = parameter.Min + (parameter.Max - parameter.Min) / 2f;
                if (parameter.IsInteger)
                {
                    mid = MathF.Round(mid);
                }

                parameter.Set(mid);
                float tolerance = parameter.IsInteger
                    ? 0.51f
                    : (parameter.Max - parameter.Min) * 0.02f + 1e-3f;
                if (Math.Abs(parameter.Get() - mid) > tolerance)
                {
                    failures.Add(
                        $"{descriptor.DisplayName}/{parameter.Name}: set {mid:0.###} read back {parameter.Get():0.###}");
                }
            }

            // --- process real audio and time it ---
            processor.Reset();
            var buffer = new float[frames * channels];
            var phase = 0.0;
            double sumSquares = 0;
            bool finite = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int block = 0; block < blocks; block++)
            {
                for (int f = 0; f < frames; f++)
                {
                    float s = 0.25f * MathF.Sin((float)phase);
                    phase += 2 * Math.PI * 1000 / sampleRate;
                    buffer[f * channels] = s;
                    buffer[f * channels + 1] = s;
                }

                processor.Process(buffer, frames);

                for (int i = 0; i < buffer.Length; i++)
                {
                    if (!float.IsFinite(buffer[i]))
                    {
                        finite = false;
                    }

                    sumSquares += (double)buffer[i] * buffer[i];
                }
            }

            clock.Stop();
            processor.Reset();

            double msPerBlock = clock.Elapsed.TotalMilliseconds / blocks;
            timings.Add((descriptor.DisplayName, msPerBlock));

            if (!finite)
            {
                failures.Add($"{descriptor.DisplayName}: produced non-finite samples");
                continue;
            }

            double rms = Math.Sqrt(sumSquares / ((double)blocks * frames * channels));
            if (rms > 8)
            {
                failures.Add($"{descriptor.DisplayName}: output ran away (RMS {rms:0.##})");
            }

            // --- preset round-trip ---
            var entry = PresetSerializer.SerializeProcessor(processor);
            var restored = PresetSerializer.CreateProcessor(entry);
            if (restored is not IParameterizedProcessor restoredParameters)
            {
                failures.Add($"{descriptor.DisplayName}: preset did not restore");
                continue;
            }

            foreach (var (name, value) in entry.Parameters)
            {
                var parameter = restoredParameters.Parameters.FirstOrDefault(p => p.Name == name);
                if (parameter == null)
                {
                    failures.Add($"{descriptor.DisplayName}/{name}: missing after restore");
                    continue;
                }

                if (Math.Abs(parameter.Get() - value) > (parameter.Max - parameter.Min) * 0.02f + 1e-3f)
                {
                    failures.Add(
                        $"{descriptor.DisplayName}/{name}: preset {value:0.###} restored {parameter.Get():0.###}");
                }
            }
        }

        bool passed = failures.Count == 0;
        var slowest = timings.OrderByDescending(t => t.Ms).FirstOrDefault();
        string cost = slowest.Name == null
            ? string.Empty
            : $" Slowest: {slowest.Name} {slowest.Ms:0.00} ms per 5 ms block.";
        string summary = passed
            ? $"{timings.Count} effects OK (parameters round-trip, output finite, presets restore).{cost}"
            : "Effects FAILED: " + string.Join("; ", failures);
        return new EffectsTestResult(passed, summary);
    }

    public sealed record BenchResult(int Sources, int Outputs, double Seconds, long FramesMixed, double CpuMs, long AllocBytes);

    /// <summary>
    /// Runs the mixer for a fixed duration with several sources and reports
    /// throughput and CPU cost. Used to spot regressions and GC churn.
    /// </summary>
    public static BenchResult Benchmark(int sources, int outputs, double seconds)
    {
        sources = Math.Max(1, sources);
        outputs = Math.Max(1, outputs);

        var src = new SourceNode[sources];
        for (int i = 0; i < sources; i++)
        {
            src[i] = new SourceNode($"bench{i}", SourceKind.Application);
            // Pre-fill with a steady tone so the mix does real work.
            var tone = new float[4800 * 2];
            for (int s = 0; s < tone.Length; s++)
            {
                tone[s] = 0.2f * MathF.Sin(s * 0.05f);
            }

            src[i].WriteTestSamples(tone);
        }

        long framesMixed = 0;
        // Preallocated feed buffer â€” the harness must not allocate either, or it
        // would pollute the allocation measurement of the engine.
        var feed = new float[480];
        Array.Fill(feed, 0.2f);

        var sinks = new OutputNode[outputs];
        for (int o = 0; o < outputs; o++)
        {
            var sink = OutputNode.CreateForTest((mix, length) =>
            {
                System.Threading.Interlocked.Add(ref framesMixed, length / 2);
                // Keep the ring buffers fed so the mix is never starved.
                for (int i = 0; i < sources; i++)
                {
                    src[i].WriteTestSamples(feed);
                }
            });

            for (int i = 0; i < sources; i++)
            {
                sink.Inputs.Add(new OutputNode.Route { Source = src[i], Gain = 1f });
            }

            sink.RefreshRoutes();
            sink.Start();
            sinks[o] = sink;
        }

        long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
        var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

        Thread.Sleep((int)(seconds * 1000));

        var cpuAfter = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        long allocAfter = GC.GetTotalAllocatedBytes(precise: false);

        for (int o = 0; o < outputs; o++)
        {
            sinks[o].Stop();
            sinks[o].Dispose();
        }

        for (int i = 0; i < sources; i++)
        {
            src[i].Dispose();
        }

        double cpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
        return new BenchResult(sources, outputs, seconds, framesMixed, cpuMs, allocAfter - allocBefore);
    }

    public sealed record LiveRouteResult(
        double Seconds,
        long CapturedFrames,
        long MixedFrames,
        long ShortReadFrames,
        double Rms,
        double Peak,
        float SourceMeter,
        string? Fault,
        string Summary);

    /// <summary>
    /// End-to-end live test: captures a real process and pushes it through the
    /// graph into a test sink paced at real time. Reports signal level, capture
    /// progress, and how many frames had to be silence-padded (underruns).
    /// </summary>
    public static LiveRouteResult TestLiveRoute(uint processId, double seconds)
    {
        const int channels = 2;
        seconds = Math.Clamp(seconds, 1.0, 30.0);

        var source = new SourceNode($"live {processId}", SourceKind.Application, processId);
        string? fault = null;
        source.Faulted += (_, reason) => fault = reason;

        double sumSquares = 0;
        double peak = 0;
        long mixedFrames = 0;

        var output = OutputNode.CreateForTest((mix, length) =>
        {
            mixedFrames += length / channels;
            for (int i = 0; i < length; i++)
            {
                double v = mix[i];
                sumSquares += v * v;
                double a = Math.Abs(v);
                if (a > peak)
                {
                    peak = a;
                }
            }
        });

        output.PaceRealTime = true;
        output.Inputs.Add(new OutputNode.Route { Source = source, Gain = 1f });
        output.RefreshRoutes();
        output.Start();
        source.Start();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds && fault == null)
        {
            Thread.Sleep(50);
        }

        double elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        long captured = source.CapturedFrames;
        long shortRead = output.ShortReadFrames;
        float meter = source.MeterLevel;

        output.Stop();
        output.Dispose();
        source.Stop();
        source.Dispose();

        long samples = Math.Max(1, mixedFrames * channels);
        double rms = Math.Sqrt(sumSquares / samples);

        string summary =
            $"Live route pid {processId}: captured {captured} frames ({captured / 48000.0:0.00} s of audio), " +
            $"mixed {mixedFrames}, underrun {shortRead} frames, RMS {rms:0.000}, peak {peak:0.000}, " +
            $"source meter {meter:0.000}" +
            (fault == null ? "." : $" | FAULT: {fault}");

        return new LiveRouteResult(
            elapsed, captured, mixedFrames, shortRead, rms, peak, meter, fault, summary);
    }

    public sealed record DeviceRouteResult(
        string Device,
        string PlaybackState,
        bool LowLatency,
        double DeviceLatencyMs,
        long BytesWritten,
        double ExpectedBytes,
        double BufferedMs,
        long CapturedFrames,
        long ShortReadFrames,
        float SourceMeter,
        string? Fault,
        string Summary);

    /// <summary>
    /// Full end-to-end test against a real render endpoint: captures a process,
    /// mixes it, and pushes it to the actual WASAPI device. Verifies that the
    /// endpoint is playing, that it consumes audio at real time, and that the
    /// render buffer stays ahead of the device instead of overflowing.
    /// </summary>
    public static DeviceRouteResult TestDeviceRoute(uint processId, double seconds)
    {
        const int channels = 2;
        const int sampleRate = 48000;
        seconds = Math.Clamp(seconds, 1.0, 30.0);

        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(
            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        string deviceName = device.FriendlyName;

        var source = new SourceNode($"live {processId}", SourceKind.Application, processId);
        string? fault = null;
        source.Faulted += (_, reason) => fault = reason;

        var output = new OutputNode(device);
        output.Inputs.Add(new OutputNode.Route { Source = source, Gain = 1f });
        output.RefreshRoutes();
        output.Start();
        source.Start();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        double bufferedSum = 0;
        int samples = 0;
        while (clock.Elapsed.TotalSeconds < seconds && fault == null)
        {
            bufferedSum += output.BufferedMs;
            samples++;
            Thread.Sleep(50);
        }

        double elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        long bytes = output.BytesWritten;
        long captured = source.CapturedFrames;
        long shortRead = output.ShortReadFrames;
        float meter = source.MeterLevel;
        string state = output.PlaybackState;
        bool lowLatency = output.LowLatencyActive;
        double deviceLatency = output.EstimatedLatencyMs;
        double avgBuffered = samples > 0 ? bufferedSum / samples : 0;
        double expectedBytes = elapsed * sampleRate * channels * 4;

        output.Stop();
        output.Dispose();
        source.Stop();
        source.Dispose();

        string summary =
            $"Device route on '{deviceName}': state {state}, low-latency {lowLatency}, " +
            $"latency {deviceLatency:0.0} ms, wrote {bytes} bytes (expected ~{expectedBytes:0}), " +
            $"avg buffered {avgBuffered:0.0} ms, captured {captured} frames, underrun {shortRead}, " +
            $"source meter {meter:0.000}" +
            (fault == null ? "." : $" | FAULT: {fault}");

        return new DeviceRouteResult(
            deviceName, state, lowLatency, deviceLatency, bytes, expectedBytes,
            avgBuffered, captured, shortRead, meter, fault, summary);
    }

    public sealed record GraphRouteResult(
        string Device,
        string PlaybackState,
        bool SourceRunning,
        bool OutputRunning,
        long BytesWritten,
        double ExpectedBytes,
        long CapturedFrames,
        float SourceMeter,
        string? Fault,
        string Summary);

    /// <summary>
    /// Mirrors the UI scenario exactly: an application source is added to the
    /// graph, connected to a device output, and the graph starts both. This
    /// exercises AudioGraph.Connect/EnsureRunning â€” the same path the window
    /// uses when a cable is dropped between two nodes.
    /// </summary>
    public static GraphRouteResult TestGraphRoute(uint processId, double seconds)
    {
        const int sampleRate = 48000;
        const int channels = 2;
        seconds = Math.Clamp(seconds, 1.0, 30.0);

        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(
            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        string deviceName = device.FriendlyName;

        using var graph = new AudioGraph();
        var source = new SourceNode($"app {processId}", SourceKind.Application, processId);
        string? fault = null;
        source.Faulted += (_, reason) => fault = reason;

        graph.AddSource(source);
        var output = graph.AddOutput(device);
        graph.Connect(source, output);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds && fault == null)
        {
            Thread.Sleep(50);
        }

        double elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        long bytes = output.BytesWritten;
        long captured = source.CapturedFrames;
        float meter = source.MeterLevel;
        string state = output.PlaybackState;
        bool sourceRunning = source.IsRunning;
        bool outputRunning = output.IsRunning;
        double expectedBytes = elapsed * sampleRate * channels * 4;

        graph.Dispose();
        source.Dispose();

        string summary =
            $"Graph route on '{deviceName}': source running {sourceRunning}, output running {outputRunning}, " +
            $"state {state}, wrote {bytes} bytes (expected ~{expectedBytes:0}), " +
            $"captured {captured} frames, source meter {meter:0.000}" +
            (fault == null ? "." : $" | FAULT: {fault}");

        return new GraphRouteResult(
            deviceName, state, sourceRunning, outputRunning, bytes, expectedBytes,
            captured, meter, fault, summary);
    }

    public sealed record ChainBreakResult(
        double LevelWithEffect,
        double LevelAfterRemoval,
        int RoutesAfterRemoval,
        string Summary);

    /// <summary>
    /// Verifies that an effect node really is in the signal path and that
    /// removing it takes the audio with it. A source wired through a gain
    /// modifier into an output must be audible at the modifier's gain, and must
    /// fall silent â€” with no leftover route â€” once the modifier is deleted.
    /// </summary>
    public static ChainBreakResult TestChainBreak()
    {
        const int channels = 2;
        const int chunkFrames = 480;

        var graph = new AudioGraph { AutoRun = false };
        var source = new SourceNode("chain source", SourceKind.Application);
        var modifier = new ModifierNode("Half gain");
        modifier.Add(new GainProcessor { Gain = 0.5f });

        bool measuringAfter = false;
        double sumWithEffect = 0;
        long framesWithEffect = 0;
        double sumAfterRemoval = 0;
        long framesAfterRemoval = 0;

        var output = graph.AddOutputForTest((mix, length) =>
        {
            for (int i = 0; i < length; i++)
            {
                double v = mix[i];
                if (measuringAfter)
                {
                    sumAfterRemoval += v * v;
                }
                else
                {
                    sumWithEffect += v * v;
                }
            }

            if (measuringAfter)
            {
                framesAfterRemoval += length / channels;
            }
            else
            {
                framesWithEffect += length / channels;
            }
        });

        graph.AddSource(source);
        graph.AddModifier(modifier);
        graph.ConnectSourceToModifier(source, modifier);
        graph.ConnectModifierToOutput(modifier, output);

        // A constant tone so the mix is not silent.
        var tone = new float[chunkFrames];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = 0.8f;
        }

        var mix = new float[chunkFrames * channels];
        var temp = new float[chunkFrames * channels];
        var bytes = new byte[chunkFrames * channels * 4];

        void Feed(int chunks)
        {
            for (int i = 0; i < chunks; i++)
            {
                source.WriteTestSamples(tone);
                source.WriteTestSamples(tone);
                output.RenderChunk(mix, temp, bytes, channels);
            }
        }

        Feed(20);

        double levelWithEffect = framesWithEffect > 0
            ? Math.Sqrt(sumWithEffect / (framesWithEffect * channels))
            : 0;

        // Now remove the effect: the chain is broken, so the source must stop
        // reaching the output entirely.
        graph.RemoveModifier(modifier);

        int routesAfter = output.Inputs.Count;
        measuringAfter = true;

        Feed(20);

        double levelAfterRemoval = framesAfterRemoval > 0
            ? Math.Sqrt(sumAfterRemoval / (framesAfterRemoval * channels))
            : 0;

        graph.Dispose();
        source.Dispose();

        bool pass = levelWithEffect > 0.3 && levelWithEffect < 0.5 && levelAfterRemoval < 0.0001 && routesAfter == 0;

        string summary =
            $"Chain break: level through effect {levelWithEffect:0.000} (expected ~0.40 = 0.80 x 0.50), " +
            $"level after removing effect {levelAfterRemoval:0.000}, routes left {routesAfter} " +
            $"-> {(pass ? "PASS" : "FAIL")}";

        return new ChainBreakResult(levelWithEffect, levelAfterRemoval, routesAfter, summary);
    }

    /// <summary>
    /// Checks that one source feeding two outputs gives both outputs the same
    /// signal. Each output renders on its own thread, so the source has to hand
    /// every consumer its own copy of the audio.
    /// </summary>
    public static string TestFanOut()
    {
        const int channels = 2;
        const int sampleRate = 48000;
        const int chunkFrames = 240;

        var source = new SourceNode("fanout", SourceKind.Application);

        // Feed the source the way a capture would: a block every 5 ms, for
        // as long as the test runs.
        var feeding = true;
        var feeder = new Thread(() =>
        {
            double phase = 0;
            var block = new float[chunkFrames * channels];
            while (feeding)
            {
                for (int i = 0; i < chunkFrames; i++)
                {
                    float v = (float)(0.5 * Math.Sin(phase));
                    block[i * channels] = v;
                    block[(i * channels) + 1] = v;
                    phase += 2 * Math.PI * 440 / sampleRate;
                }

                source.WriteTestSamples(block);
                Thread.Sleep(5);
            }
        })
        {
            IsBackground = true,
            Name = "FanOutFeeder",
        };

        double levelA = 0;
        double levelB = 0;

        var outputA = OutputNode.CreateForTest((mix, length) =>
        {
            for (int i = 0; i < length; i++)
            {
                levelA += Math.Abs(mix[i]);
            }
        });

        var outputB = OutputNode.CreateForTest((mix, length) =>
        {
            for (int i = 0; i < length; i++)
            {
                levelB += Math.Abs(mix[i]);
            }
        });

        outputA.PaceRealTime = true;
        outputB.PaceRealTime = true;
        outputA.Inputs.Add(new OutputNode.Route { Source = source, Gain = 1f });
        outputB.Inputs.Add(new OutputNode.Route { Source = source, Gain = 1f });
        outputA.RefreshRoutes();
        outputB.RefreshRoutes();

        outputA.Start();
        outputB.Start();
        feeder.Start();
        Thread.Sleep(1500);
        feeding = false;
        feeder.Join(1000);

        double buffered = source.BufferedMs;
        outputA.Stop();
        outputB.Stop();
        outputA.Dispose();
        outputB.Dispose();
        source.Dispose();

        // Both outputs should have received roughly the same energy.
        double balance = Math.Abs(levelA - levelB) / Math.Max(levelA, levelB);
        bool pass = levelA > 1.0 && levelB > 1.0 && balance < 0.1;

        return $"Fan-out: output A {levelA:0.0}, output B {levelB:0.0}, difference {balance * 100:0.0}%, " +
               $"source backlog {buffered:0} ms, skipped {source.SkippedFrames} frames " +
               $"-> {(pass ? "PASS" : "FAIL")}";
    }

    /// <summary>
    /// Checks that a saved chain (subgraph) builds the right processors, runs
    /// them in order, and survives a preset round-trip.
    /// </summary>
    public static string TestChainProcessor()
    {
        var definition = new SubgraphDefinition { Name = "Test chain" };
        definition.Elements.Add(new SubgraphElement
        {
            TypeId = GainProcessor.Id,
            Parameters = { ["Gain"] = 0.5f },
        });
        definition.Elements.Add(new SubgraphElement
        {
            TypeId = HighPassProcessor.Id,
            Parameters = { ["Cutoff"] = 200f },
        });

        var chain = definition.CreateProcessor();
        if (chain.Processors.Count != 2)
        {
            return $"chain: built {chain.Processors.Count} effects, expected 2 -> FAIL";
        }

        // A constant signal has no high-frequency content, so the high-pass
        // inside the chain must flatten it completely.
        var buffer = new float[512];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = 1f;
        }

        chain.Process(buffer, 256);

        // A high-pass needs a few samples to settle, so look at the tail of
        // the block rather than the transient at the start.
        double peak = 0;
        for (int j = buffer.Length - 64; j < buffer.Length; j++)
        {
            peak = Math.Max(peak, Math.Abs(buffer[j]));
        }

        // The chain must survive being written to a preset and read back.
        var entry = PresetSerializer.SerializeProcessor(chain);
        var restored = PresetSerializer.CreateProcessor(entry) as ChainProcessor;
        bool roundTrip = restored != null && restored.Processors.Count == 2;

        bool pass = peak < 0.01 && roundTrip;
        return $"chain: {chain.Processors.Count} effects, DC peak after chain {peak:0.0000}, " +
               $"preset round-trip {roundTrip} -> {(pass ? "PASS" : "FAIL")}";
    }

    /// <summary>
    /// Measures the delay a listener actually hears: it captures the target
    /// process the way the router does (process loopback) and simultaneously
    /// captures the render endpoint's loopback (what comes out of the
    /// speakers), then cross-correlates the two.
    ///
    /// The difference between the two is the whole chain: the app's own
    /// buffering plus whatever the app being captured does internally.
    /// </summary>
    public static string TestEndToEndDelay(uint processId, double seconds)
    {
        seconds = Math.Clamp(seconds, 2.0, 30.0);

        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var renderDevice = enumerator.GetDefaultAudioEndpoint(
            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);

        var processSamples = new List<float>(48000 * 8);
        var deviceSamples = new List<float>(48000 * 8);
        int processPackets = 0;
        double processPeak = 0;
        int devicePackets = 0;

        // A: the process, tapped exactly like the router taps it.
        var processCapture = new ProcessLoopbackCapture(processId);
        processCapture.DataAvailable += (_, e) =>
        {
            lock (processSamples)
            {
                processPackets++;
                int count = e.BytesRecorded / 2;
                for (int i = 0; i < count; i += 2)
                {
                    float l = BitConverter.ToInt16(e.Buffer, i * 2) * (1f / 32768f);
                    float r = BitConverter.ToInt16(e.Buffer, (i + 1) * 2) * (1f / 32768f);
                    processPeak = Math.Max(processPeak, Math.Max(Math.Abs(l), Math.Abs(r)));
                    processSamples.Add((l + r) * 0.5f);
                }
            }
        };

        // B: the speakers, as the listener hears them.
        var recorder = new NAudio.Wave.WasapiRecorderBuilder()
            .WithDevice(renderDevice)
            .WithSharedMode()
            .WithEventSync()
            .WithLoopbackCapture()
            .Build();

        recorder.DataAvailable += (span, flags, devPos, qpcPos) =>
        {
            if (span.Length == 0)
            {
                return;
            }

            // The endpoint's mix format may be float or 32-bit int, and it can
            // be WAVE_FORMAT_EXTENSIBLE, so decode by bits rather than by
            // casting blindly.
            lock (deviceSamples)
            {
                devicePackets++;
                if (recorder.WaveFormat.BitsPerSample == 32 &&
                    recorder.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat)
                {
                    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                    for (int i = 0; i + 1 < floats.Length; i += 2)
                    {
                        deviceSamples.Add((floats[i] + floats[i + 1]) * 0.5f);
                    }
                }
                else if (recorder.WaveFormat.BitsPerSample == 32)
                {
                    var ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(span);
                    for (int i = 0; i + 1 < ints.Length; i += 2)
                    {
                        deviceSamples.Add((ints[i] / 2147483648f + ints[i + 1] / 2147483648f) * 0.5f);
                    }
                }
                else
                {
                    var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(span);
                    for (int i = 0; i + 1 < shorts.Length; i += 2)
                    {
                        deviceSamples.Add((shorts[i] / 32768f + shorts[i + 1] / 32768f) * 0.5f);
                    }
                }
            }
        };

        processCapture.Start();
        recorder.StartRecording();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(100);
        }

        recorder.StopRecording();
        recorder.Dispose();
        processCapture.Stop();
        processCapture.Dispose();

        float[] a;
        float[] b;
        lock (processSamples)
        {
            a = processSamples.ToArray();
        }

        lock (deviceSamples)
        {
            b = deviceSamples.ToArray();
        }

        if (a.Length < 48000 || b.Length < 48000)
        {
            return $"delay: not enough audio (process {a.Length} samples, device {b.Length} samples) -> inconclusive";
        }

        // Energy envelope at 1 ms resolution.
        const int hop = 48; // 1 ms at 48 kHz
        int aBins = a.Length / hop;
        int bBins = b.Length / hop;
        var envA = new float[aBins];
        var envB = new float[bBins];

        for (int i = 0; i < aBins; i++)
        {
            envA[i] = Envelope(a, i, hop);
        }

        for (int i = 0; i < bBins; i++)
        {
            envB[i] = Envelope(b, i, hop);
        }

        // Match click onsets rather than correlating envelopes: a sparse
        // signal makes envelope correlation ambiguous (many lags score alike).
        var onsetsA = FindOnsets(envA);
        var onsetsB = FindOnsets(envB);

        if (onsetsA.Count < 3 || onsetsB.Count < 3)
        {
            float maxA = 0, maxB = 0;
            foreach (float v in envA) if (v > maxA) maxA = v;
            foreach (float v in envB) if (v > maxB) maxB = v;
            return $"delay: too few onsets to align (process {onsetsA.Count} peak {maxA:0.000}, " +
                   $"device {onsetsB.Count} peak {maxB:0.000}) | " +
                   $"process packets {processPackets} raw peak {processPeak:0.0000} format {processCapture.WaveFormat}, " +
                   $"device packets {devicePackets} format {recorder.WaveFormat} -> inconclusive";
        }

        var offsets = new List<int>();
        foreach (int ta in onsetsA)
        {
            int best = int.MaxValue;
            int bestDelta = 0;
            foreach (int tb in onsetsB)
            {
                int delta = tb - ta;
                if (delta < -2500 || delta > 2500)
                {
                    continue;
                }

                if (Math.Abs(delta) < Math.Abs(bestDelta) || best == int.MaxValue)
                {
                    best = tb;
                    bestDelta = delta;
                }
            }

            if (best != int.MaxValue)
            {
                offsets.Add(bestDelta);
            }
        }

        if (offsets.Count < 3)
        {
            return "delay: could not align onsets -> inconclusive";
        }

        offsets.Sort();
        int median = offsets[offsets.Count / 2];
        int min = offsets[0];
        int max = offsets[^1];

        // The trend: is the delay growing (a starved audio thread) or fixed?
        var trend = new List<string>();
        int half = offsets.Count / 2;
        int early = offsets[Math.Max(0, half / 2)];
        int late = offsets[Math.Min(offsets.Count - 1, half + (half / 2))];
        trend.Add($"first={early}ms last={late}ms");

        return $"delay (process tap -> speakers): median {median} ms, range {min}..{max} ms over {offsets.Count} clicks, " +
               $"trend {string.Join(" ", trend)}; " +
               $"process audio {a.Length / 48000.0:0.0} s, speaker audio {b.Length / 48000.0:0.0} s";
    }

    /// <summary>Finds the rising edges of bursts in a 1 ms energy envelope.</summary>
    private static List<int> FindOnsets(float[] envelope)
    {
        float max = 0;
        foreach (float v in envelope)
        {
            if (v > max)
            {
                max = v;
            }
        }

        var onsets = new List<int>();
        float threshold = Math.Max(0.01f, max * 0.25f);
        int last = -100;

        for (int i = 1; i < envelope.Length; i++)
        {
            if (envelope[i] > threshold && envelope[i - 1] <= threshold && i - last > 40)
            {
                onsets.Add(i);
                last = i;
            }
        }

        return onsets;
    }

    private static float Envelope(float[] samples, int bin, int hop)
    {
        double sum = 0;
        int start = bin * hop;
        for (int j = 0; j < hop && start + j < samples.Length; j++)
        {
            float v = samples[start + j];
            if (!float.IsFinite(v))
            {
                continue;
            }

            sum += v * v;
        }

        return (float)Math.Sqrt(sum / hop);
    }

    /// <summary>
    /// Measures where the delay in a live route comes from: the capture ring
    /// filling up, the mixer consuming at a different rate, or a one-off offset
    /// created at startup.
    /// </summary>
    public static string TestLatencyProfile(uint processId, double seconds)
    {
        seconds = Math.Clamp(seconds, 1.0, 30.0);

        var source = new SourceNode($"latency {processId}", SourceKind.Application, processId);
        long consumed = 0;

        var output = OutputNode.CreateForTest((mix, length) =>
        {
            consumed += length / 2;
        });

        output.PaceRealTime = true;
        output.Inputs.Add(new OutputNode.Route { Source = source, Gain = 1f });
        output.RefreshRoutes();
        output.Start();
        source.Start();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var samples = new List<string>();

        while (clock.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(250);
            double elapsed = clock.Elapsed.TotalSeconds;
            long captured = source.CapturedFrames;
            samples.Add(
                $"t={elapsed:0.00}s captured={captured / 48000.0 * 1000:0}ms consumed={consumed / 48000.0 * 1000:0}ms " +
                $"ring={source.BufferedMs:0}ms meter={source.MeterLevel:0.00}");
        }

        output.Stop();
        output.Dispose();
        source.Stop();
        source.Dispose();

        long finalCaptured = source.CapturedFrames;
        double drift = (finalCaptured - consumed) / 48000.0 * 1000.0;

        return "latency profile: " + string.Join(" | ", samples) +
               $" | drift (captured-consumed) {drift:0} ms, final ring {source.BufferedMs:0} ms";
    }

    /// <summary>
    /// Reports the ASIO drivers on this machine and, when there is one, opens it
    /// and renders the mix through it for a moment. Drivers whose host is not
    /// running have no clock, so "no clock" is reported rather than failed.
    /// </summary>
    public static string TestAsioDevices()
    {
        var drivers = AsioCatalog.Discover();
        if (drivers.Count == 0)
        {
            return "asio: no ASIO drivers installed -> PASS (nothing to drive)";
        }

        var lines = new List<string>();
        bool anyStarted = false;
        bool allOpened = true;

        foreach (var driver in drivers)
        {
            string line = $"asio: '{driver.Name}' {driver.InputChannelCount} in / {driver.OutputChannelCount} out, " +
                          $"{driver.SampleRate} Hz, buffer {driver.BufferSize} frames, " +
                          $"latency {driver.OutputLatencySamples} out / {driver.InputLatencySamples} in";

            try
            {
                var graph = new AudioGraph { AutoRun = false };
                var output = graph.AddOutput(driver);
                output.Start();

                // Start() waits for the first buffer; let a few more flow so
                // the callback count and FlexASIO's log prove sustained
                // streaming rather than a single kick from ASIOStart.
                if (output.AsioStreaming)
                {
                    Thread.Sleep(150);
                }

                line += $", state {output.PlaybackState}, rate {output.EndpointSampleRate} Hz, " +
                        $"callbacks {output.AsioCallbackCount}, buffered {output.BufferedMs:0} ms, " +
                        $"latency {output.EstimatedLatencyMs:0.0} ms";

                // The driver needs a clock from its host; without one it stays
                // configured but never streams. That is a driver condition, not
                // a bug in the app, so it is reported rather than failed.
                if (output.AsioStreaming)
                {
                    anyStarted = true;
                }
                else
                {
                    line += " (no clock: is the driver host running?)";
                }

                output.Stop();
            }
            catch (Exception ex)
            {
                allOpened = false;
                line += $" -> FAILED: {ex.Message}";
            }

            lines.Add(line);
        }

        lines.Add(TestAsioRateConversion());

        string verdict = anyStarted ? "PASS" : allOpened ? "PASS (driver present, idle)" : "FAIL";
        return string.Join(" | ", lines) + " -> " + verdict;
    }

    /// <summary>
    /// Checks the 48 kHz mixer to driver-rate conversion: the frame count must
    /// follow the rate ratio and the tone must keep its pitch.
    /// </summary>
    private static string TestAsioRateConversion()
    {
        const int mixRate = 48000;
        const int targetRate = 44100;
        const double toneHz = 440.0;

        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(mixRate, 2);
        var buffer = new NAudio.Wave.BufferedWaveProvider(format, TimeSpan.FromSeconds(2));

        int frames = mixRate;
        var tone = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * toneHz * i / mixRate));
            tone[i * 2] = v;
            tone[(i * 2) + 1] = v;
        }

        buffer.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tone.AsSpan()));

        var provider = new AsioFloatProvider(buffer, targetRate);
        var outBytes = new byte[targetRate * 2 * 4];
        int total = 0;
        while (total < outBytes.Length)
        {
            int read = provider.Read(outBytes, total, outBytes.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        int outFrames = total / 8;

        // Measure the pitch of the converted signal.
        var samples = new float[outFrames * 2];
        outBytes.AsSpan(0, total).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));

        int crossings = 0;
        double peak = 0;
        for (int i = 1; i < outFrames; i++)
        {
            if ((samples[(i * 2) - 2] < 0) != (samples[(i * 2)] < 0))
            {
                crossings++;
            }
        }

        for (int i = 0; i < outFrames * 2; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        double measuredHz = crossings > 0 ? crossings * targetHz() / 2.0 / outFrames : 0;

        int expected = (int)Math.Round(frames * (targetRate / (double)mixRate));
        bool rateOk = Math.Abs(outFrames - expected) <= expected / 100 + 8;
        bool pitchOk = Math.Abs(measuredHz - toneHz) < 2.0 && peak > 0.2;

        return $"asio rate: 48k -> {targetRate / 1000.0:0.0}k gave {outFrames} frames (expected {expected}), " +
               $"peak {peak:0.000}, tone {measuredHz:0.0} Hz -> {(rateOk && pitchOk ? "PASS" : "FAIL")}";

        static double targetHz() => 44100;
    }

    /// <summary>
    /// Writes the same short tone in every supported format, then reads each
    /// file back through the matching decoder. Proves the file writer and the
    /// Media Foundation encoders, not just that a file appeared.
    /// </summary>
    public static string TestFileRecorder()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ruse-record-test");
        var results = new List<string>();
        bool allPassed = true;

        try
        {
            if (System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }

            System.IO.Directory.CreateDirectory(folder);

            const int totalFrames = 14400; // 0.3 s
            const int chunkFrames = 240;

            foreach (var info in AudioFileFormats.All)
            {
                string path = System.IO.Path.Combine(folder, "tone." + info.Extension);
                bool passed = false;
                double seconds = 0;
                float peak = 0;
                string detail = string.Empty;

                try
                {
                    using (var recorder = new AudioFileRecorder(path, info.Format))
                    {
                        var block = new float[chunkFrames * 2];
                        for (int start = 0; start < totalFrames; start += chunkFrames)
                        {
                            for (int frame = 0; frame < chunkFrames; frame++)
                            {
                                float value = (float)(0.5 * Math.Sin(
                                    2 * Math.PI * 440 * (start + frame) / 48000.0));
                                block[frame * 2] = value;
                                block[frame * 2 + 1] = value;
                            }

                            recorder.Write(block, block.Length);
                        }

                        recorder.Stop();
                        if (recorder.LastError != null)
                        {
                            detail = recorder.LastError;
                        }
                    }

                    if (string.IsNullOrEmpty(detail) && System.IO.File.Exists(path))
                    {
                        using WaveStream reader = info.Format is AudioFileFormat.Mp3 or AudioFileFormat.Flac
                            ? new MediaFoundationReader(path)
                            : new WaveFileReader(path);

                        int expectedBits = info.Format switch
                        {
                            AudioFileFormat.Wav16 => 16,
                            AudioFileFormat.Wav24 => 24,
                            AudioFileFormat.Wav32Float => 32,
                            _ => 0,
                        };

                        bool formatOk = expectedBits == 0 ||
                            (reader is WaveFileReader wav && wav.WaveFormat.BitsPerSample == expectedBits);
                        bool encodingOk = info.Format != AudioFileFormat.Wav32Float ||
                            (reader is WaveFileReader floatWav &&
                             floatWav.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat);

                        var samples = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
                        int read = reader.ToSampleProvider().Read(samples.AsSpan());
                        for (int i = 0; i < read; i++)
                        {
                            peak = Math.Max(peak, Math.Abs(samples[i]));
                        }

                        // Very short FLAC files report TotalTime=0 in Media
                        // Foundation even though the decoded samples are there.
                        seconds = read / (double)(reader.WaveFormat.SampleRate * reader.WaveFormat.Channels);
                        passed = formatOk && encodingOk && read > 0 &&
                                 seconds > 0.2 && seconds < 0.8 && peak > 0.1;
                    }
                    else if (string.IsNullOrEmpty(detail))
                    {
                        detail = "file was not created";
                    }
                }
                catch (Exception ex)
                {
                    detail = ex.GetType().Name + ": " + ex.Message;
                }

                allPassed &= passed;
                results.Add($"{info.DisplayName}: {seconds:0.00}s peak {peak:0.00}" +
                            (passed ? string.Empty : $" ({detail})"));
            }

            // The writer test above is direct. This one runs the real output
            // pump for a moment, so the node path (open, pace, write, finalize)
            // is covered too. With no routes it records silence.
            string nodePath = System.IO.Path.Combine(folder, "node.wav");
            bool nodePassed = false;
            string nodeDetail = string.Empty;
            double nodeSeconds = 0;
            long nodeFrames = 0;
            try
            {
                using (var output = OutputNode.CreateForFile(nodePath, AudioFileFormat.Wav16))
                {
                    output.Start();
                    Thread.Sleep(350);
                    output.Stop();
                    nodeSeconds = output.RecordedSeconds;
                    nodeDetail = output.RecordingError ?? string.Empty;
                }

                if (string.IsNullOrEmpty(nodeDetail) && System.IO.File.Exists(nodePath))
                {
                    using var reader = new WaveFileReader(nodePath);
                    nodeFrames = reader.Length / reader.WaveFormat.BlockAlign;
                }

                nodePassed = string.IsNullOrEmpty(nodeDetail) && nodeSeconds > 0.2 && nodeFrames > 0;
            }
            catch (Exception ex)
            {
                nodeDetail = ex.GetType().Name + ": " + ex.Message;
            }

            allPassed &= nodePassed;
            results.Add($"node pump: {nodeSeconds:0.00}s {nodeFrames} frames" +
                        (nodePassed ? string.Empty : $" ({nodeDetail})"));

            // Finally, feed a route while the pump is running. This is the
            // path a real source takes: subscription -> mix -> recorder.
            string routePath = System.IO.Path.Combine(folder, "route.wav");
            bool routePassed = false;
            string routeDetail = string.Empty;
            double routeSeconds = 0;
            float routePeak = 0;
            try
            {
                using var graph = new AudioGraph { AutoRun = false };
                var source = new SourceNode("Recorder test tone", SourceKind.Application);
                var output = graph.AddOutputForFile(routePath, AudioFileFormat.Wav16);
                graph.AddSource(source);
                graph.Connect(source, output);
                output.Start();

                var subscription = output.Inputs[0].Subscription;
                var block = new float[960 * 2]; // 20 ms
                for (int part = 0; part < 14; part++)
                {
                    for (int frame = 0; frame < 960; frame++)
                    {
                        float value = (float)(0.5 * Math.Sin(
                            2 * Math.PI * 440 * ((part * 960) + frame) / 48000.0));
                        block[frame * 2] = value;
                        block[frame * 2 + 1] = value;
                    }

                    subscription.Write(block);
                    Thread.Sleep(20);
                }

                output.Stop();
                routeSeconds = output.RecordedSeconds;
                routeDetail = output.RecordingError ?? string.Empty;

                if (string.IsNullOrEmpty(routeDetail) && System.IO.File.Exists(routePath))
                {
                    using var reader = new WaveFileReader(routePath);
                    var samples = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
                    int read = reader.ToSampleProvider().Read(samples.AsSpan());
                    for (int i = 0; i < read; i++)
                    {
                        routePeak = Math.Max(routePeak, Math.Abs(samples[i]));
                    }
                }

                routePassed = string.IsNullOrEmpty(routeDetail) &&
                              routeSeconds > 0.15 && routePeak > 0.1;
            }
            catch (Exception ex)
            {
                routeDetail = ex.GetType().Name + ": " + ex.Message;
            }

            allPassed &= routePassed;
            results.Add($"route to file: {routeSeconds:0.00}s peak {routePeak:0.00}" +
                        (routePassed ? string.Empty : $" ({routeDetail})"));
        }
        catch (Exception ex)
        {
            allPassed = false;
            results.Add("setup failed: " + ex.Message);
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
            }
            catch
            {
            }
        }

        return string.Join(" | ", results) + " -> " + (allPassed ? "PASS" : "FAIL");
    }
}
