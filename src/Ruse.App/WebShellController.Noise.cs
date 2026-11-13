using System.Windows.Threading;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private DispatcherTimer? _noiseCalibrationTimer;
    private string? _noiseCalibrationNodeId;

    private void StartNoiseCalibration(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<NoiseSuppressionProcessor>().FirstOrDefault() is not { } noise)
        {
            ShowError("That noise-suppression node is no longer on the canvas.");
            return;
        }

        if (!noise.StartLiveCalibration(3f))
        {
            ShowError("Noise calibration is already running.");
            return;
        }

        _noiseCalibrationNodeId = id;
        _noiseCalibrationTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        if (_noiseCalibrationTimer.Tag == null)
        {
            _noiseCalibrationTimer.Tag = true;
            _noiseCalibrationTimer.Tick += OnNoiseCalibrationTick;
        }

        _noiseCalibrationTimer.Start();
        SetStatus("Calibrating noise floor: keep the room quiet for 3 seconds.");
    }

    private void OnNoiseCalibrationTick(object? sender, EventArgs e)
    {
        if (_noiseCalibrationNodeId is not { } id ||
            !_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<NoiseSuppressionProcessor>().FirstOrDefault() is not { } noise)
        {
            StopNoiseCalibration();
            return;
        }

        if (noise.IsCalibrating)
        {
            return;
        }

        StopNoiseCalibration();
        PublishGraph();
        PublishTelemetry(force: true);
        SetStatus(noise.NoiseProfileError is { } error
            ? $"Noise calibration failed: {error}"
            : "Noise floor calibrated from live audio.",
            isError: noise.NoiseProfileError != null);
    }

    private void StopNoiseCalibration()
    {
        _noiseCalibrationTimer?.Stop();
        _noiseCalibrationNodeId = null;
    }
}
