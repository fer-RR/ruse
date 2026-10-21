using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Captures the rendered audio of a single process (and its children) using the
/// Windows process-loopback activation API (Windows 10 build 20348+).
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const int AudclntShareModeShared = 0;
    private const uint AudclntStreamflagLoopback = 0x00020000;
    private const uint AudclntStreamflagEventCallback = 0x00040000;
    private const uint AudclntStreamflagAutoConvertPcm = 0x80000000;
    private const uint AudclntBufferflagsSilent = 0x2;

    private readonly AutoResetEvent _stopEvent = new(false);
    private Thread? _thread;
    private volatile bool _running;

    // Formats offered to the engine, in order of preference. The process
    // loopback endpoint accepts a plain 16-bit PCM WAVEFORMATEX; 48 kHz matches
    // the engine's internal rate, 44.1 kHz is the format the Microsoft sample
    // documents as always safe.
    private static readonly WaveFormat[] CaptureFormatCandidates =
    {
        new WaveFormat(48000, 16, 2),
        new WaveFormat(44100, 16, 2),
    };

    /// <summary>Writes a plain WAVEFORMATEX (18 bytes) for a PCM format.</summary>
    private static IntPtr AllocWaveFormat(WaveFormat format)
    {
        short blockAlign = (short)(format.Channels * format.BitsPerSample / 8);
        IntPtr p = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(p, 0, 1); // wFormatTag = WAVE_FORMAT_PCM
        Marshal.WriteInt16(p, 2, (short)format.Channels);
        Marshal.WriteInt32(p, 4, format.SampleRate);
        Marshal.WriteInt32(p, 8, format.SampleRate * blockAlign);
        Marshal.WriteInt16(p, 12, blockAlign);
        Marshal.WriteInt16(p, 14, (short)format.BitsPerSample);
        Marshal.WriteInt16(p, 16, 0); // cbSize
        return p;
    }

    // Reused packet buffer + event args so the capture thread doesn't allocate
    // a fresh byte[]/WaveInEventArgs for every packet (steady state: zero alloc).
    private byte[] _packetBuffer = new byte[16384];
    private WaveInEventArgs? _packetArgs;

    public WaveFormat? WaveFormat { get; private set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public uint ProcessId { get; }
    public bool IncludeProcessTree { get; }

    public ProcessLoopbackCapture(uint processId, bool includeProcessTree = true)
    {
        ProcessId = processId;
        IncludeProcessTree = includeProcessTree;
    }

    public static bool IsSupported => OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 20348;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(CaptureThread) { IsBackground = true, Name = "ProcessLoopbackCapture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _stopEvent.Set();
        _thread?.Join(3000);
    }

    private void CaptureThread()
    {
        Exception? error = null;
        try
        {
            ActivateAndCapture();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private void ActivateAndCapture()
    {
        // The endpoint format is not discoverable (process-loopback clients do
        // not implement GetMixFormat/IsFormatSupported), so it is supplied
        // explicitly â€” the same approach as Microsoft's Application Loopback
        // sample. Try the engine's native rate first, then the CD rate that the
        // sample documents as always safe. Each attempt activates a fresh
        // client, because a failed Initialize can leave the interface unusable.
        Exception? lastError = null;

        foreach (var candidate in CaptureFormatCandidates)
        {
            var audioClient = ActivateClient();
            try
            {
                int hr = InitializeClient(audioClient, candidate);
                if (hr < 0)
                {
                    lastError = new InvalidOperationException($"IAudioClient.Initialize failed (0x{hr:X8}).");
                    continue;
                }

                WaveFormat = candidate;
                CaptureLoop(audioClient, candidate);
                return;
            }
            finally
            {
                Marshal.ReleaseComObject(audioClient);
            }
        }

        throw lastError ?? new InvalidOperationException("Process loopback capture could not be initialized.");
    }

    private IAudioClient ActivateClient()
    {
        var handler = new ActivationHandler();

        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = ProcessId,
                ProcessLoopbackMode = IncludeProcessTree
                    ? ProcessLoopbackMode.IncludeTargetProcessTree
                    : ProcessLoopbackMode.ExcludeTargetProcessTree,
            },
        };

        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr pBlob = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, pParams, false);

            var blob = new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = pParams,
            };
            Marshal.StructureToPtr(blob, pBlob, false);

            var iid = ProcessLoopbackInterop.IID_IAudioClient;
            ProcessLoopbackInterop.ActivateAudioInterfaceAsync(
                ProcessLoopbackInterop.VirtualAudioDeviceProcessLoopback,
                ref iid,
                pBlob,
                handler,
                out _);

            handler.WaitForCompletion();
            return handler.GetAudioClient();
        }
        finally
        {
            Marshal.FreeHGlobal(pParams);
            Marshal.FreeHGlobal(pBlob);
        }
    }

    private static int InitializeClient(IAudioClient audioClient, WaveFormat format)
    {
        IntPtr formatPtr = AllocWaveFormat(format);
        try
        {
            // The endpoint only accepts this flag combination: the mix format
            // is float, so the engine has to convert to the requested PCM
            // format. Buffer duration and periodicity are left at 0, which
            // asks the engine for its own defaults.
            uint streamFlags = AudclntStreamflagLoopback
                | AudclntStreamflagEventCallback
                | AudclntStreamflagAutoConvertPcm;

            return audioClient.Initialize(AudclntShareModeShared, streamFlags, 0, 0, formatPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }
    }

    private void CaptureLoop(IAudioClient audioClient, WaveFormat format)
    {
        int hr;

        var bufferReady = new AutoResetEvent(false);
        hr = audioClient.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle());
        if (hr < 0)
        {
            throw new InvalidOperationException($"SetEventHandle failed (0x{hr:X8}).");
        }

        var captureIid = typeof(IAudioCaptureClient).GUID;
        hr = audioClient.GetService(ref captureIid, out var captureObj);
        if (hr < 0)
        {
            throw new InvalidOperationException($"GetService(IAudioCaptureClient) failed (0x{hr:X8}).");
        }

        var captureClient = (IAudioCaptureClient)captureObj;

        hr = audioClient.Start();
        if (hr < 0)
        {
            throw new InvalidOperationException($"IAudioClient.Start failed (0x{hr:X8}).");
        }

        try
        {
            while (_running)
            {
                bool signaled = bufferReady.WaitOne(500, false);
                if (!_running)
                {
                    break;
                }

                if (!signaled)
                {
                    continue;
                }

                ReadPackets(captureClient, format);
            }
        }
        finally
        {
            audioClient.Stop();
        }
    }

    private void ReadPackets(IAudioCaptureClient captureClient, WaveFormat format)
    {
        int hr = captureClient.GetNextPacketSize(out uint packetSize);
        while (hr >= 0 && packetSize > 0 && _running)
        {
            hr = captureClient.GetBuffer(out IntPtr data, out uint framesAvailable, out uint flags, out _, out _);
            if (hr < 0)
            {
                break;
            }

            int count = (int)framesAvailable * format.BlockAlign;
            if (count > 0)
            {
                if (_packetBuffer.Length < count)
                {
                    int grown = Math.Max(count, _packetBuffer.Length * 2);
                    _packetBuffer = new byte[grown];
                    _packetArgs = null;
                }

                var buffer = _packetBuffer;
                if ((flags & AudclntBufferflagsSilent) != 0)
                {
                    Array.Clear(buffer, 0, count);
                }
                else
                {
                    Marshal.Copy(data, buffer, 0, count);
                }

                _packetArgs = new WaveInEventArgs(buffer, count);
                DataAvailable?.Invoke(this, _packetArgs);
            }

            captureClient.ReleaseBuffer(framesAvailable);
            hr = captureClient.GetNextPacketSize(out packetSize);
        }
    }

    public void Dispose()
    {
        Stop();
        _stopEvent.Dispose();
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEvent _completed = new(false);
        private IAudioClient? _audioClient;
        private int _hr = unchecked((int)0x80004005);

        public void WaitForCompletion()
        {
            if (!_completed.WaitOne(10000))
            {
                throw new TimeoutException("Timed out waiting for audio interface activation.");
            }
        }

        public IAudioClient GetAudioClient()
        {
            if (_hr < 0 || _audioClient == null)
            {
                throw new InvalidOperationException($"Audio interface activation failed (0x{_hr:X8}).");
            }

            return _audioClient;
        }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(out int activateResult, out object activatedInterface);
                if (hr < 0)
                {
                    _hr = hr;
                }
                else if (activateResult < 0)
                {
                    _hr = activateResult;
                }
                else
                {
                    _audioClient = (IAudioClient)activatedInterface;
                    _hr = 0;
                }
            }
            catch (Exception ex)
            {
                _hr = ex.HResult;
            }
            finally
            {
                _completed.Set();
            }

            return 0;
        }
    }
}
