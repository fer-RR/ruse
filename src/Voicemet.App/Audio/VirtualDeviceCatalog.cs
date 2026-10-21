using NAudio.CoreAudioApi;

namespace Voicemet.App.Audio;

public sealed class VirtualOutputInfo
{
    public required string RenderDeviceId { get; init; }
    public required string RenderFriendlyName { get; init; }
    public required string CaptureFriendlyName { get; init; }
    public required string Provider { get; init; }

    public string Description => $"{RenderFriendlyName}  →  select \"{CaptureFriendlyName}\" as mic in apps";
}

/// <summary>
/// Discovers virtual audio devices (Voicemeeter, VB-Cable, etc.) that can be used
/// to feed a mix into other applications such as Discord or OBS.
/// </summary>
public static class VirtualDeviceCatalog
{
    private static readonly (string Match, string Capture, string Provider)[] KnownPatterns =
    {
        ("Voicemeeter Input", "Voicemeeter Out B1", "Voicemeeter"),
        ("Voicemeeter AUX Input", "Voicemeeter Out B2", "Voicemeeter"),
        ("Voicemeeter VAIO3 Input", "Voicemeeter Out B3", "Voicemeeter"),
        ("CABLE Input", "CABLE Output", "VB-Cable"),
        ("Line 1 (Virtual Audio Cable)", "Line 1 (Virtual Audio Cable)", "Virtual Audio Cable"),
    };

    /// <summary>
    /// Rebuilds a <see cref="VirtualOutputInfo"/> for a previously saved render
    /// device. Falls back to name-based matching (and then the saved values)
    /// when the device ID has changed since the preset was written.
    /// </summary>
    public static VirtualOutputInfo ResolveRender(string deviceId, string title, string? micHint)
    {
        foreach (var info in Discover())
        {
            if (string.Equals(info.RenderDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        if (!string.IsNullOrEmpty(title))
        {
            var byName = Discover().FirstOrDefault(i =>
                string.Equals(i.RenderFriendlyName, title, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName;
            }
        }

        return new VirtualOutputInfo
        {
            RenderDeviceId = deviceId,
            RenderFriendlyName = string.IsNullOrEmpty(title) ? "Virtual output" : title,
            CaptureFriendlyName = string.IsNullOrEmpty(micHint) ? title : micHint,
            Provider = "Virtual device",
        };
    }

    public static IReadOnlyList<VirtualOutputInfo> Discover()
    {
        var results = new List<VirtualOutputInfo>();
        using var enumerator = new MMDeviceEnumerator();

        var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => d.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)
                     || d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
                     || d.FriendlyName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var render in renders)
        {
            var pattern = KnownPatterns.FirstOrDefault(p =>
                render.FriendlyName.Contains(p.Match, StringComparison.OrdinalIgnoreCase));

            string capture = pattern.Capture ?? render.FriendlyName;
            string provider = pattern.Provider ?? "Virtual device";

            results.Add(new VirtualOutputInfo
            {
                RenderDeviceId = render.ID,
                RenderFriendlyName = render.FriendlyName,
                CaptureFriendlyName = capture,
                Provider = provider,
            });
        }

        return results
            .OrderBy(r => r.Provider)
            .ThenBy(r => r.RenderFriendlyName)
            .ToList();
    }
}
