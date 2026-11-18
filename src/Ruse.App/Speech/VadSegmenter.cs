using Ruse.App.Audio;

namespace Ruse.App;

internal sealed record SpeechSegment(float[] Samples, double StartSeconds, double EndSeconds);

/// <summary>
/// Turns a 16 kHz speech tap into endpointed speech segments. Silero VAD is used
/// when its model is available; otherwise the same energy fallback as the
/// original chunker is used.
/// </summary>
internal sealed class VadSegmenter : IDisposable
{
    private const int FrameSamples = SileroVad.WindowSamples;
    private const int SampleRate = SileroVad.SampleRate;
    private const int PreRollFrames = 10;       // 320 ms
    private const int MinSpeechFrames = 6;      // 192 ms
    private const int HangoverFrames = 16;      // 512 ms
    private const int MaxSegmentSamples = SampleRate * 15;
    private const float StartProbability = 0.5f;
    private const float EndProbability = 0.35f;
    private const float MinVoiceRms = 0.0002f;

    private readonly SpeechTap _tap;
    private readonly SileroVad? _silero;
    private readonly float[] _frame = new float[FrameSamples];
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _segment = new();
    private int _frameFill;
    private long _readSamples;
    private long _segmentStartSample;
    private int _voicedFrames;
    private int _silentFrames;
    private bool _speaking;
    private float _noiseFloor = 0.0001f;
    private long _tapGeneration;

    public VadSegmenter(int tapId, SileroVad? silero = null)
    {
        _tap = SpeechTapRegistry.TryGet(tapId, out var tap)
            ? tap
            : SpeechTapRegistry.GetOrAdd(tapId);
        _silero = silero;
        _tapGeneration = _tap.Generation;
    }

    public event Action? SpeechStarted;
    public event Action<float[], long>? FrameAvailable;
    public event Action? SpeechEnded;

    public async Task<SpeechSegment?> ReadAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_tap.Generation != _tapGeneration)
            {
                _tapGeneration = _tap.Generation;
                Reset();
            }

            while (_frameFill < FrameSamples)
            {
                int read = _tap.Read(_frame.AsSpan(_frameFill, FrameSamples - _frameFill));
                if (read <= 0)
                {
                    await Task.Delay(20, token);
                    break;
                }

                _frameFill += read;
            }

            if (_frameFill < FrameSamples) continue;
            _frameFill = 0;
            _readSamples += FrameSamples;
            FrameAvailable?.Invoke(_frame, _readSamples - FrameSamples);

            bool voiced = IsVoiced(_frame);
            if (voiced)
            {
                _silentFrames = 0;
                _voicedFrames++;
            }
            else
            {
                _silentFrames++;
                if (!_speaking)
                {
                    _voicedFrames = 0;
                    UpdateNoiseFloor(_frame);
                }
            }

            if (!_speaking)
            {
                EnqueuePreRoll(_frame);
                if (voiced && _voicedFrames >= MinSpeechFrames)
                {
                    StartSegment();
                    SpeechStarted?.Invoke();
                }
            }
            else
            {
                Append(_frame);
                if (!voiced && _silentFrames >= HangoverFrames)
                {
                    var segment = EndSegment();
                    if (segment != null)
                    {
                        SpeechEnded?.Invoke();
                        return segment;
                    }
                }
                else if (_segment.Count >= MaxSegmentSamples)
                {
                    var segment = EndSegment(keepSpeaking: true);
                    if (segment != null) return segment;
                }
            }
        }

        return null;
    }

    private bool IsVoiced(ReadOnlySpan<float> frame)
    {
        if (_silero != null)
        {
            float probability = _silero.Process(frame);
            return _speaking ? probability >= EndProbability : probability >= StartProbability;
        }

        double energy = 0;
        for (int i = 0; i < frame.Length; i++) energy += frame[i] * frame[i];
        float rms = MathF.Sqrt((float)(energy / Math.Max(1, frame.Length)));
        return rms > MathF.Max(MinVoiceRms, _noiseFloor * 2.5f);
    }

    private void UpdateNoiseFloor(ReadOnlySpan<float> frame)
    {
        double energy = 0;
        for (int i = 0; i < frame.Length; i++) energy += frame[i] * frame[i];
        float rms = MathF.Sqrt((float)(energy / Math.Max(1, frame.Length)));
        _noiseFloor = Math.Clamp(_noiseFloor * 0.995f + rms * 0.005f, 0.00002f, 0.02f);
    }

    private void EnqueuePreRoll(float[] frame)
    {
        _preRoll.Enqueue((float[])frame.Clone());
        while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
    }

    private void StartSegment()
    {
        _segment.Clear();
        _segmentStartSample = Math.Max(0, _readSamples - (_preRoll.Count * FrameSamples));
        foreach (var frame in _preRoll) _segment.AddRange(frame);
        _preRoll.Clear();
        _speaking = true;
        _silentFrames = 0;
    }

    private void Append(float[] frame) => _segment.AddRange(frame);

    private SpeechSegment? EndSegment(bool keepSpeaking = false)
    {
        if (_segment.Count == 0) return null;
        var samples = _segment.ToArray();
        double start = _segmentStartSample / (double)SampleRate;
        double end = (start + samples.Length / (double)SampleRate);
        _segment.Clear();
        _speaking = keepSpeaking;
        _voicedFrames = 0;
        _silentFrames = 0;
        _segmentStartSample = _readSamples;
        if (keepSpeaking)
        {
            // Continue with a short overlap so the next segment does not clip
            // the first phoneme after the split.
            int overlap = Math.Min(FrameSamples, samples.Length);
            _segment.AddRange(samples.AsSpan(samples.Length - overlap).ToArray());
            _segmentStartSample = _readSamples - overlap;
        }
        return new SpeechSegment(samples, start, end);
    }

    public void Reset()
    {
        _frameFill = 0;
        _readSamples = 0;
        _segmentStartSample = 0;
        _voicedFrames = 0;
        _silentFrames = 0;
        _speaking = false;
        _noiseFloor = 0.0001f;
        _preRoll.Clear();
        _segment.Clear();
        _silero?.Reset();
        _tapGeneration = _tap.Generation;
    }

    public void Dispose() => _silero?.Dispose();
}
