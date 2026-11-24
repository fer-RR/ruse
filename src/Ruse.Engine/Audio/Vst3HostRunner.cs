using System.IO.Pipes;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>Runs a VST3 plugin in an isolated child process for the main shell.</summary>
public static class Vst3HostRunner
{
    public static void Run(string audioPipeName, string controlPipeName, string identifier,
        string? stateBase64 = null, string? handshakeToken = null, bool allowInstrument = false)
    {
        try
        {
            using var audio = new NamedPipeClientStream(".", audioPipeName, PipeDirection.InOut);
            using var control = new NamedPipeClientStream(".", controlPipeName, PipeDirection.InOut);
            audio.Connect(15000);
            control.Connect(15000);

            using var shared = Vst3SharedAudio.Open(audioPipeName);
            using var processor = new Vst3Processor(
                identifier, stateBase64, start: true, allowInstrument: allowInstrument);
            var audioThread = new Thread(() => AudioLoop(processor, shared))
            {
                IsBackground = true,
                Name = "Vst3HostAudio",
            };
            audioThread.Start();

            while (true)
            {
                var request = Vst3HostProtocol.ReadRequest(control);
                if (request == null)
                {
                    break;
                }

                var response = new Vst3HostResponse();
                try
                {
                    switch (request.Command)
                    {
                        case "hello":
                            response.Ok = !string.IsNullOrEmpty(handshakeToken) &&
                                          string.Equals(request.Name, handshakeToken, StringComparison.Ordinal);
                            if (!response.Ok)
                            {
                                response.Error = "Handshake failed.";
                            }

                            break;
                        case "info":
                            response.Ok = true;
                            response.Info = BuildInfo(processor);
                            break;
                        case "set":
                            var parameter = processor.Parameters.FirstOrDefault(item =>
                                string.Equals(item.Name, request.Name, StringComparison.Ordinal));
                            if (parameter == null)
                            {
                                response.Ok = false;
                                response.Error = "Unknown parameter.";
                            }
                            else
                            {
                                parameter.Set(request.Value);
                                response.Ok = true;
                            }

                            break;
                        case "state":
                            response.Ok = true;
                            response.State = processor.CaptureStateBase64();
                            break;
                        case "load-state":
                            response.Ok = processor.ApplyStateBase64(request.State ?? "");
                            break;
                        case "open-editor":
                            response.Ok = processor.OpenEditor("VST3 editor");
                            break;
                        case "close-editor":
                            processor.CloseEditor();
                            response.Ok = true;
                            break;
                        case "stop":
                            response.Ok = true;
                            Vst3HostProtocol.WriteControl(control, response);
                            return;
                        default:
                            response.Ok = false;
                            response.Error = "Unknown command.";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    response.Ok = false;
                    response.Error = ex.GetBaseException().Message;
                    AppLog.Warning("VST3 host command failed: " + ex.Message);
                }

                Vst3HostProtocol.WriteControl(control, response);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The isolated VST3 host stopped.", ex);
        }
    }

    private static void AudioLoop(Vst3Processor processor, Vst3SharedAudio shared)
    {
        using var mmcss = MmcssScope.Enter();
        var buffer = new float[Vst3SharedAudio.MaxFrames * Vst3SharedAudio.Channels];
        var midi = new Vst3MidiEvent[Vst3SharedAudio.MaxMidiEvents];
        double lastTempo = 120.0;
        bool lastPlaying = false;
        int lastReset = 0;
        try
        {
            while (true)
            {
                if (!shared.Receive(buffer, midi, out int frames, out int midiCount))
                {
                    return;
                }

                double tempo = shared.ReadTempo();
                if (Math.Abs(tempo - lastTempo) > 0.001)
                {
                    processor.SetTempo(tempo);
                    lastTempo = tempo;
                }

                int transportReset = shared.ReadTransportReset();
                if (transportReset != lastReset)
                {
                    processor.ResetTransportPosition();
                    lastReset = transportReset;
                }

                bool playing = shared.ReadTransportState();
                if (playing != lastPlaying)
                {
                    processor.SetTransportState(playing);
                    lastPlaying = playing;
                }

                for (int i = 0; i < midiCount; i++)
                {
                    Vst3MidiEvent value = midi[i];
                    processor.EnqueueMidi(value.Status, value.Data1, value.Data2, value.SampleOffset);
                }

                int samples = frames * Vst3SharedAudio.Channels;
                processor.Process(buffer.AsSpan(0, samples), frames);
                shared.Send(buffer, frames);
            }
        }
        catch (Exception ex)
        {
            if (ex is not EndOfStreamException && ex is not IOException)
            {
                AppLog.Error("The isolated VST3 audio thread stopped.", ex);
            }
        }
    }


    private static Vst3HostInfo BuildInfo(Vst3Processor processor)
    {
        var info = new Vst3HostInfo
        {
            Name = processor.PluginName,
            Vendor = processor.Vendor,
            Version = processor.Version,
            Status = processor.Status,
            Error = processor.Error,
            IsReady = processor.IsReady,
            IsInstrument = processor.IsInstrument,
            HasEditor = processor.HasEditor,
            IsEditorOpen = processor.IsEditorOpen,
            LatencySamples = processor.LatencySamples,
        };

        foreach (var parameter in processor.Parameters)
        {
            info.Parameters.Add(new Vst3HostParameter
            {
                Name = parameter.Name,
                Min = parameter.Min,
                Max = parameter.Max,
                Default = parameter.Default,
                Value = parameter.Get(),
                Unit = parameter.Unit,
                IsToggle = parameter.IsToggle,
                IsChoice = parameter.IsChoice,
                Options = parameter.Options,
            });
        }

        return info;
    }
}
