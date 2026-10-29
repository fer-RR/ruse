using System.IO.Pipes;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>Runs a VST3 plugin in an isolated child process for the main shell.</summary>
public static class Vst3HostRunner
{
    public static void Run(string audioPipeName, string controlPipeName, string identifier, string? stateBase64 = null)
    {
        try
        {
            using var audio = new NamedPipeClientStream(".", audioPipeName, PipeDirection.InOut);
            using var control = new NamedPipeClientStream(".", controlPipeName, PipeDirection.InOut);
            audio.Connect(15000);
            control.Connect(15000);

            using var processor = new Vst3Processor(identifier, stateBase64, start: true);
            var audioThread = new Thread(() => AudioLoop(audio, processor))
            {
                IsBackground = true,
                Name = "Vst3HostAudio",
            };
            audioThread.Start();

            var buffer = new float[8192];
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

    private static void AudioLoop(NamedPipeClientStream stream, Vst3Processor processor)
    {
        var buffer = new float[8192];
        try
        {
            while (true)
            {
                if (!Vst3HostProtocol.ReadAudio(stream, buffer, out int frames, out int channels, out _))
                {
                    return;
                }

                int samples = frames * channels;
                processor.Process(buffer.AsSpan(0, samples), frames);
                Vst3HostProtocol.WriteAudio(stream, buffer.AsSpan(0, samples), frames, channels,
                    Vst3HostProtocol.CommandProcess);
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
