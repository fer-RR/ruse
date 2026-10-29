using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ruse.App.Audio;

/// <summary>Wire format shared by the VST3 proxy and its child process.</summary>
public static class Vst3HostProtocol
{
    public const int CommandProcess = 1;
    public const int CommandStop = 2;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void WriteControl(Stream stream, Vst3HostRequest request)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
    }

    public static void WriteControl(Stream stream, Vst3HostResponse response)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
    }

    public static Vst3HostRequest? ReadRequest(Stream stream)
    {
        var payload = ReadControlPayload(stream);
        return JsonSerializer.Deserialize<Vst3HostRequest>(payload, Json);
    }

    public static Vst3HostResponse? ReadControl(Stream stream)
    {
        var payload = ReadControlPayload(stream);
        return JsonSerializer.Deserialize<Vst3HostResponse>(payload, Json);
    }

    private static byte[] ReadControlPayload(Stream stream)
    {
        Span<byte> length = stackalloc byte[4];
        ReadExactly(stream, length);
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size <= 0 || size > 2_000_000)
        {
            throw new InvalidDataException("Invalid VST3 host message length.");
        }

        var payload = new byte[size];
        ReadExactly(stream, payload);
        return payload;
    }

    public static void WriteAudio(Stream stream, ReadOnlySpan<float> samples, int frames, int channels, int command)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, command);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], frames);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], channels);
        stream.Write(header);
        if (command == CommandProcess)
        {
            stream.Write(MemoryMarshal.AsBytes(samples));
        }
    }

    public static bool ReadAudio(Stream stream, Span<float> destination, out int frames, out int channels, out int command)
    {
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        command = BinaryPrimitives.ReadInt32LittleEndian(header);
        frames = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        channels = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (command != CommandProcess)
        {
            return false;
        }

        int samples = frames * channels;
        if (samples < 0 || samples > destination.Length)
        {
            throw new InvalidDataException("Invalid VST3 audio block size.");
        }

        ReadExactly(stream, MemoryMarshal.AsBytes(destination[..samples]));
        return true;
    }

    public static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int read = stream.Read(buffer);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            buffer = buffer[read..];
        }
    }
}

public sealed class Vst3HostRequest
{
    public string Command { get; set; } = "info";
    public string? Name { get; set; }
    public float Value { get; set; }
    public string? State { get; set; }
}

public sealed class Vst3HostResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public Vst3HostInfo? Info { get; set; }
    public string? State { get; set; }
}

public sealed class Vst3HostInfo
{
    public string Name { get; set; } = "VST3 plugin";
    public string Vendor { get; set; } = "";
    public string Version { get; set; } = "";
    public string Status { get; set; } = "Loading plugin";
    public string? Error { get; set; }
    public bool IsReady { get; set; }
    public bool HasEditor { get; set; }
    public bool IsEditorOpen { get; set; }
    public int LatencySamples { get; set; }
    public List<Vst3HostParameter> Parameters { get; set; } = new();
}

public sealed class Vst3HostParameter
{
    public string Name { get; set; } = "";
    public float Min { get; set; }
    public float Max { get; set; }
    public float Default { get; set; }
    public float Value { get; set; }
    public string Unit { get; set; } = "";
    public bool IsToggle { get; set; }
    public bool IsChoice { get; set; }
    public string[] Options { get; set; } = Array.Empty<string>();
}
