namespace Ruse.App.Audio;

public sealed partial class TimelineService : IDisposable
{
    private const int GrainSize = 2048;
    private const int GrainHop = GrainSize / 2;
    private static readonly float[] GrainWindow = BuildGrainWindow();
    private static void MixTrack(TimelineTrack track, Dictionary<string, ClipPlayback> clipPlayback,
        long position, int frames, float[] block)
    {
        foreach (var clip in track.Clips)
        {
            if (!clipPlayback.TryGetValue(clip.Id, out var state))
            {
                state = new ClipPlayback();
                clipPlayback[clip.Id] = state;
            }

            MixClipInto(clip, state, track.Gain, position, frames, block, 0);
        }
    }

    private static void MixClipInto(TimelineClip clip, ClipPlayback state, float trackGain,
        long position, int frames, float[] block, int blockOffset)
    {
        if (clip.Audio == null || clip.LengthSamples <= 0 || frames <= 0)
        {
            return;
        }

        long clipEnd = clip.StartSample + clip.LengthSamples;
        long blockEnd = position + frames;
        long mixStart = Math.Max(position, clip.StartSample);
        long mixEnd = Math.Min(blockEnd, clipEnd);
        if (mixEnd <= mixStart)
        {
            return;
        }

        int audioFrames = clip.Audio.Length / 2;
        if (audioFrames <= 0)
        {
            return;
        }

        int writeOffset = (int)(mixStart - position);
        int writeFrames = (int)(mixEnd - mixStart);
        bool pitched = clip.PitchAutomationSemitones.HasValue ||
            Math.Abs(clip.PitchSemitones) > 0.0001f;
        bool reset = !state.Initialized ||
            state.LastEndSample != mixStart ||
            state.LastStartSample != clip.StartSample ||
            state.LastSourceOffset != clip.SourceOffsetSamples ||
            state.LastAudioLength != clip.Audio.Length ||
            state.GranularMode != pitched;
        if (reset)
        {
            double sourceStart = clip.SourceOffsetSamples + (mixStart - clip.StartSample);
            state.SourceCursor = sourceStart;
            state.CurrentRatio = PitchRatio(EffectivePitch(clip));
            state.GrainA = new GrainState { Age = 0, ReadPos = sourceStart };
            state.GrainB = new GrainState { Age = GrainHop, ReadPos = sourceStart };
        }

        state.Initialized = true;
        state.GranularMode = pitched;
        state.LastStartSample = clip.StartSample;
        state.LastSourceOffset = clip.SourceOffsetSamples;
        state.LastAudioLength = clip.Audio.Length;

        double loopStart = Math.Clamp(clip.SourceOffsetSamples, 0, audioFrames - 1);
        double loopLength = Math.Max(1, audioFrames - loopStart);
        float targetRatio = PitchRatio(EffectivePitch(clip));
        int targetIndex = (blockOffset + writeOffset) * 2;
        int fadeIn = Math.Max(0, clip.FadeInSamples);
        int fadeOut = Math.Max(0, clip.FadeOutSamples);
        long clipFrame = mixStart - clip.StartSample;
        int played = 0;
        for (int frame = 0; frame < writeFrames; frame++)
        {
            long localFrame = clipFrame + frame;
            float left;
            float right;
            if (pitched)
            {
                state.CurrentRatio = ParameterSmoothing.Approach(
                    state.CurrentRatio, targetRatio, ParameterSmoothing.FastAlpha);
                double anchor = clip.SourceOffsetSamples + mixStart - clip.StartSample + frame;
                if (clip.Loop)
                {
                    if (anchor >= audioFrames)
                    {
                        anchor = loopStart + ((anchor - loopStart) % loopLength);
                    }
                }

                var grainA = ReadGrain(ref state.GrainA, anchor, state.CurrentRatio,
                    clip.Audio, audioFrames, clip.Loop, loopStart, loopLength);
                var grainB = ReadGrain(ref state.GrainB, anchor, state.CurrentRatio,
                    clip.Audio, audioFrames, clip.Loop, loopStart, loopLength);
                left = grainA.Left + grainB.Left;
                right = grainA.Right + grainB.Right;
            }
            else
            {
                double cursor = state.SourceCursor;
                if (clip.Loop)
                {
                    if (cursor < loopStart)
                    {
                        cursor = loopStart;
                    }
                    else if (cursor >= audioFrames)
                    {
                        cursor = loopStart + ((cursor - loopStart) % loopLength);
                    }
                }
                else if (cursor >= audioFrames)
                {
                    break;
                }

                int index = Math.Max(0, (int)cursor);
                int next = index + 1;
                if (next >= audioFrames)
                {
                    next = clip.Loop ? (int)loopStart : index;
                }

                float blend = (float)(cursor - index);
                int sourceIndex = index * 2;
                int nextIndex = next * 2;
                left = clip.Audio[sourceIndex] * (1f - blend) + clip.Audio[nextIndex] * blend;
                right = clip.Audio[sourceIndex + 1] * (1f - blend) + clip.Audio[nextIndex + 1] * blend;
                state.SourceCursor = cursor + 1;
            }

            float gain = clip.Gain * trackGain;
            if (fadeIn > 0 && localFrame < fadeIn)
            {
                gain *= localFrame / (float)fadeIn;
            }

            long tail = clip.LengthSamples - localFrame;
            if (fadeOut > 0 && tail < fadeOut)
            {
                gain *= Math.Max(0f, tail / (float)fadeOut);
            }

            block[targetIndex] += left * gain;
            block[targetIndex + 1] += right * gain;
            targetIndex += 2;
            played++;
        }

        state.LastEndSample = mixStart + played;
    }

    private static (float Left, float Right) ReadGrain(ref GrainState grain, double anchor,
        float ratio, float[] audio, int audioFrames, bool loop, double loopStart, double loopLength)
    {
        if (grain.Age >= GrainSize)
        {
            grain.Age = 0;
            grain.ReadPos = anchor;
        }

        float window = GrainWindow[grain.Age];
        double pos = grain.ReadPos;
        grain.ReadPos += ratio;
        grain.Age++;
        if (loop)
        {
            if (pos < loopStart)
            {
                pos = loopStart;
            }
            else if (pos >= audioFrames)
            {
                pos = loopStart + ((pos - loopStart) % loopLength);
            }
        }
        else if (pos < 0 || pos >= audioFrames)
        {
            return (0f, 0f);
        }

        int index = Math.Max(0, (int)pos);
        int next = index + 1;
        if (next >= audioFrames)
        {
            next = loop ? (int)loopStart : index;
        }

        float blend = (float)(pos - index);
        int sourceIndex = index * 2;
        int nextIndex = next * 2;
        float left = audio[sourceIndex] * (1f - blend) + audio[nextIndex] * blend;
        float right = audio[sourceIndex + 1] * (1f - blend) + audio[nextIndex + 1] * blend;
        return (left * window, right * window);
    }

    private static float[] BuildGrainWindow()
    {
        var window = new float[GrainSize];
        for (int i = 0; i < GrainSize; i++)
        {
            window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / GrainSize);
        }

        return window;
    }

    private static float EffectivePitch(TimelineClip clip) =>
        clip.PitchAutomationSemitones ?? clip.PitchSemitones;

    private static float PitchRatio(float semitones) =>
        MathF.Pow(2f, Math.Clamp(semitones,
            -MaxClipPitchSemitones, MaxClipPitchSemitones) / 12f);
    private sealed class ClipPlayback
    {
        public bool Initialized;
        public long LastEndSample = long.MinValue;
        public long LastStartSample = long.MinValue;
        public long LastSourceOffset = long.MinValue;
        public int LastAudioLength = -1;
        public bool GranularMode;
        public double SourceCursor;
        public float CurrentRatio = 1f;
        public GrainState GrainA;
        public GrainState GrainB;

        public void Reset()
        {
            Initialized = false;
            LastEndSample = long.MinValue;
            LastStartSample = long.MinValue;
            LastSourceOffset = long.MinValue;
            LastAudioLength = -1;
            GranularMode = false;
            SourceCursor = 0;
            CurrentRatio = 1f;
            GrainA = default;
            GrainB = default;
        }
    }

    private struct GrainState
    {
        public int Age;
        public double ReadPos;
    }
}
