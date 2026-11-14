using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>
/// Global mixer-group state. Group processing is a gain/mute/solo stage that
/// runs separately from each member's own fader, so group moves do not
/// overwrite per-node levels.
/// </summary>
public static class MixerGroupBus
{
    private sealed class State
    {
        public volatile float Gain = 1f;
        public volatile bool Muted;
        public volatile bool Solo;
    }

    private static readonly ConcurrentDictionary<string, State> Groups =
        new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _anySourceSolo;
    private static volatile bool _anyOutputSolo;

    public static bool AnySourceSolo => _anySourceSolo;
    public static bool AnyOutputSolo => _anyOutputSolo;

    public static void Configure(IReadOnlyDictionary<string, MixerGroupSettings>? groups)
    {
        Groups.Clear();
        if (groups == null)
        {
            RefreshSoloFlags();
            return;
        }

        foreach (var (key, value) in groups)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            Groups[key] = new State
            {
                Gain = Math.Clamp(value.Gain, 0f, 2f),
                Muted = value.Muted,
                Solo = value.Solo,
            };
        }

        RefreshSoloFlags();
    }

    public static float Gain(string? key) =>
        !string.IsNullOrEmpty(key) && Groups.TryGetValue(key, out var state) ? state.Gain : 1f;

    public static bool Muted(string? key) =>
        !string.IsNullOrEmpty(key) && Groups.TryGetValue(key, out var state) && state.Muted;

    public static bool Solo(string? key) =>
        !string.IsNullOrEmpty(key) && Groups.TryGetValue(key, out var state) && state.Solo;

    private static void RefreshSoloFlags()
    {
        bool source = false;
        bool output = false;
        foreach (var (key, state) in Groups)
        {
            if (!state.Solo) continue;
            if (key.StartsWith("source:", StringComparison.OrdinalIgnoreCase)) source = true;
            else if (key.StartsWith("output:", StringComparison.OrdinalIgnoreCase)) output = true;
        }

        _anySourceSolo = source;
        _anyOutputSolo = output;
    }




    public static void SetGain(string key, float gain)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var state = Groups.GetOrAdd(key, static _ => new State());
        state.Gain = Math.Clamp(gain, 0f, 2f);
    }

    public static void SetMuted(string key, bool muted)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        Groups.GetOrAdd(key, static _ => new State()).Muted = muted;
    }

    public static void SetSolo(string key, bool solo)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        Groups.GetOrAdd(key, static _ => new State()).Solo = solo;
        RefreshSoloFlags();
    }

    public static MixerGroupSettings Snapshot(string key) =>
        Groups.TryGetValue(key, out var state)
            ? new MixerGroupSettings { Gain = state.Gain, Muted = state.Muted, Solo = state.Solo }
            : new MixerGroupSettings();
}
