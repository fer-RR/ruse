using System.Text;
using System.Text.RegularExpressions;

namespace Ruse.App;

/// <summary>One timed piece of text, used by the live views and subtitle exports.</summary>
internal sealed record SubtitleCue(double StartSeconds, double EndSeconds, string Text);

/// <summary>Energy VAD plus sentence cut points for the live speech pipeline.</summary>
internal sealed class SpeechChunker
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 160;          // 10 ms
    private const int MinSamples = SampleRate * 2; // avoid one-word chunks
    private const int MaxSamples = SampleRate * 10;
    private const int HangoverFrames = 120;        // 1.2 s of silence ends a sentence
    private const int MinVoiceFrames = 20;         // require 200 ms of voice

    private readonly List<float> _samples = new(MaxSamples + 8192);
    private int _silentFrames;
    private int _voicedFrames;
    private int _frameCount;
    private float _frameEnergy;
    private float _noiseFloor = 0.0005f;
    private long _chunkStartSample;
    private long _totalSamples;

    public void Clear()
    {
        ResetChunk();
        _chunkStartSample = 0;
        _totalSamples = 0;
    }

    private void ResetChunk()
    {
        _samples.Clear();
        _silentFrames = 0;
        _voicedFrames = 0;
        _frameCount = 0;
        _frameEnergy = 0;
        _noiseFloor = 0.0005f;
        _chunkStartSample = _totalSamples;
    }

    public bool Add(ReadOnlySpan<float> samples, out float[] chunk, out double startSeconds)
    {
        chunk = Array.Empty<float>();
        startSeconds = 0;
        if (samples.IsEmpty)
        {
            return false;
        }

        if (_samples.Count == 0)
        {
            _chunkStartSample = _totalSamples;
        }

        _totalSamples += samples.Length;
        foreach (float sample in samples)
        {
            _samples.Add(sample);
            _frameEnergy += sample * sample;
            _frameCount++;
            if (_frameCount < FrameSamples)
            {
                continue;
            }

            float rms = MathF.Sqrt(_frameEnergy / FrameSamples);
            float threshold = MathF.Max(0.001f, _noiseFloor * 2.5f);
            if (rms > threshold)
            {
                _silentFrames = 0;
                _voicedFrames++;
            }
            else
            {
                _silentFrames++;
            }

            _noiseFloor = Math.Clamp(_noiseFloor * 0.995f + rms * 0.005f, 0.0005f, 0.02f);
            _frameCount = 0;
            _frameEnergy = 0;
        }

        bool enough = _samples.Count >= MinSamples && _voicedFrames >= MinVoiceFrames;
        bool atLimit = _samples.Count >= MaxSamples;
        bool silenceCut = enough && _silentFrames >= HangoverFrames;
        if (!atLimit && !silenceCut)
        {
            return false;
        }

        if (!enough)
        {
            // A long silent or noisy-only stretch should not become a Whisper job.
            ResetChunk();
            return false;
        }

        chunk = _samples.ToArray();
        startSeconds = _chunkStartSample / (double)SampleRate;
        ResetChunk();
        return true;
    }
}

internal static class SentenceSplitter
{
    private static readonly Regex Boundary = new(@"(?<=[.!?ã€‚ï¼ï¼Ÿ])\s+", RegexOptions.Compiled);

    public static List<SubtitleCue> Split(string text, double startSeconds, double endSeconds)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return new List<SubtitleCue>();
        }

        string[] parts = Boundary.Split(text)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        if (parts.Length <= 1)
        {
            return new List<SubtitleCue> { new(startSeconds, Math.Max(startSeconds, endSeconds), text) };
        }

        double start = Math.Max(0, startSeconds);
        double end = Math.Max(start + 0.01, endSeconds);
        int totalChars = parts.Sum(part => part.Length);
        var cues = new List<SubtitleCue>(parts.Length);
        double cursor = start;
        for (int i = 0; i < parts.Length; i++)
        {
            double span = (end - start) * parts[i].Length / Math.Max(1, totalChars);
            double cueEnd = i == parts.Length - 1 ? end : Math.Min(end, cursor + span);
            cues.Add(new SubtitleCue(cursor, cueEnd, parts[i]));
            cursor = cueEnd;
        }

        return cues;
    }

    /// <summary>Gives translated sentences the timing of their source sentences when they align.</summary>
    public static List<SubtitleCue> Align(IReadOnlyList<SubtitleCue> source, string translatedText)
    {
        if (source.Count == 0 || translatedText.Trim().Length == 0)
        {
            return new List<SubtitleCue>();
        }

        var translated = Split(translatedText, source[0].StartSeconds, source[^1].EndSeconds);
        if (translated.Count == source.Count)
        {
            return source.Select((cue, index) =>
                new SubtitleCue(cue.StartSeconds, cue.EndSeconds, translated[index].Text)).ToList();
        }

        return translated;
    }
}

internal static class SubtitleExporter
{
    public static string Srt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        int index = 1;
        foreach (var cue in cues.OrderBy(cue => cue.StartSeconds))
        {
            builder.AppendLine(index.ToString());
            builder.AppendLine($"{FormatSrt(cue.StartSeconds)} --> {FormatSrt(cue.EndSeconds)}");
            builder.AppendLine(cue.Text);
            builder.AppendLine();
            index++;
        }

        return builder.ToString();
    }

    public static string Vtt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues.OrderBy(cue => cue.StartSeconds))
        {
            builder.AppendLine($"{FormatVtt(cue.StartSeconds)} --> {FormatVtt(cue.EndSeconds)}");
            builder.AppendLine(cue.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatSrt(double seconds) => FormatTime(seconds, ',');

    private static string FormatVtt(double seconds) => FormatTime(seconds, '.');

    private static string FormatTime(double seconds, char separator)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        int hours = (int)time.TotalHours;
        return $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";
    }
}

