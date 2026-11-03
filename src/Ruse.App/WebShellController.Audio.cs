using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void ConfigureAudioEngine()
    {
        bool auto = _settings.AudioAutoMode;
        double blockMs = auto ? 5.0 : Math.Clamp(_settings.AudioBlockSizeMs, 2.5, 10.0);
        int block = AudioEngineConfig.FramesForMs(blockMs);

        bool cushionAuto = auto || !string.Equals(
            _settings.EngineCushionMode, "manual", StringComparison.OrdinalIgnoreCase);
        int cushion = cushionAuto
            ? block * 2
            : Math.Max(block, AudioEngineConfig.CushionFramesForMs(_settings.EngineCushionManualMs));

        string mode = auto ? "balanced" : _settings.OutputBufferMode?.Trim().ToLowerInvariant() ?? "balanced";
        int periods = mode switch
        {
            "low" => 1,
            "safe" => 3,
            "manual" => 0,
            _ => 2,
        };
        int manualMs = mode == "manual" ? Math.Clamp(_settings.OutputBufferManualMs, 2, 40) : 0;

        AudioEngineConfig.Configure(block, cushion, periods == 0 ? 2 : periods, manualMs);
        AudioEngineConfig.ElasticRing = !auto && _settings.ElasticRing;
        OutputNode.AggressiveCushion = !auto && mode == "low";
    }
}
