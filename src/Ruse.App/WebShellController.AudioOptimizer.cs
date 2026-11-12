using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private AudioOptimizerStateDto _audioOptimizerState = new();
    private int _audioOptimizerBusy;

    private void StartAudioOptimizer()
    {
        if (Interlocked.Exchange(ref _audioOptimizerBusy, 1) != 0)
        {
            PostAudioOptimizerState();
            return;
        }

        _ = RunAudioOptimizerAsync();
    }

    private async Task RunAudioOptimizerAsync()
    {
        bool wasRunning = _running;
        try
        {
            SetAudioOptimizerState("running", 0, "Preparing audio optimization\u2026");
            if (wasRunning)
            {
                StopRouting();
            }

            var result = await Task.Run(() => AudioConfigOptimizer.Run(
                (progress, message) => PostAudioOptimizerProgress(progress, message)));

            await _window.Dispatcher.InvokeAsync(() =>
            {
                ApplyAudioOptimizerResult(result);
                if (wasRunning)
                {
                    StartRouting();
                }

                AudioConfigOptimizer.Trial winner = result.Winner;
                SetAudioOptimizerState("done", 1,
                    winner.Clean
                        ? $"Applied {winner.Candidate.Label}."
                        : $"Applied the most stable profile found: {winner.Candidate.Label}.",
                    result: ToDto(result));
                SetStatus(winner.Clean
                    ? $"Audio optimized: {winner.Candidate.Label}."
                    : $"Audio optimization finished with dropouts; {winner.Candidate.Label} was the most stable choice.",
                    isError: !winner.Clean);
                PublishSettings();
            });
        }
        catch (Exception ex)
        {
            await _window.Dispatcher.InvokeAsync(() =>
            {
                ConfigureAudioEngine();
                if (wasRunning)
                {
                    StartRouting();
                }

                SetAudioOptimizerState("error", 0, "Audio optimization failed.", ex.Message);
                SetStatus("Audio optimization failed: " + ex.Message, isError: true);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _audioOptimizerBusy, 0);
        }
    }

    private void ApplyAudioOptimizerResult(AudioConfigOptimizer.Result result)
    {
        AudioConfigOptimizer.Trial winner = result.Winner;
        _settings.AudioAutoMode = false;
        _settings.LowLatencyMode = false;
        _settings.AudioBlockSizeMs = winner.Candidate.BlockMs;
        _settings.OutputBufferMode = winner.Candidate.OutputBufferMode;
        if (winner.Candidate.OutputBufferManualMs > 0)
        {
            _settings.OutputBufferManualMs = winner.Candidate.OutputBufferManualMs;
        }

        _settings.EngineCushionMode = "manual";
        _settings.EngineCushionManualMs = (int)Math.Round(winner.Candidate.EngineCushionMs);
        _settings.ElasticRing = winner.Candidate.ElasticRing;
        _settings.AggressiveOutputCushion = winner.Candidate.OutputBufferMode == "low";
        _settings.Save();
        ConfigureAudioEngine();
    }

    private void PostAudioOptimizerProgress(double progress, string message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                SetAudioOptimizerState("running",
                    Math.Clamp(progress, 0, 1),
                    message);
            }));
        }
        catch
        {
        }
    }

    private void PostAudioOptimizerState()
    {
        Post("audio-optimizer", _audioOptimizerState);
    }

    private void SetAudioOptimizerState(string status, double progress, string message,
        string? error = null, AudioOptimizerResultDto? result = null)
    {
        _audioOptimizerState = new AudioOptimizerStateDto
        {
            Status = status,
            Progress = progress,
            Message = message,
            Error = error,
            Result = result,
        };
        PostAudioOptimizerState();
    }

    private static AudioOptimizerResultDto ToDto(AudioConfigOptimizer.Result result)
    {
        return new AudioOptimizerResultDto
        {
            Label = result.Winner.Candidate.Label,
            BlockMs = result.Winner.Candidate.BlockMs,
            OutputBufferMode = result.Winner.Candidate.OutputBufferMode,
            OutputBufferManualMs = result.Winner.Candidate.OutputBufferManualMs,
            EngineCushionMs = result.Winner.Candidate.EngineCushionMs,
            ElasticRing = result.Winner.Candidate.ElasticRing,
            MaxLatencyMs = result.Winner.MaxLatencyMs,
            Underruns = result.Winner.Underruns,
            ShortReadFrames = result.Winner.ShortReadFrames,
            SkippedFrames = result.Winner.SkippedFrames,
            Clean = result.Winner.Clean,
            Trials = result.Trials.Select(trial => new AudioOptimizerTrialDto
            {
                Label = trial.Candidate.Label,
                BlockMs = trial.Candidate.BlockMs,
                OutputBufferMode = trial.Candidate.OutputBufferMode,
                OutputBufferManualMs = trial.Candidate.OutputBufferManualMs,
                EngineCushionMs = trial.Candidate.EngineCushionMs,
                MaxLatencyMs = trial.MaxLatencyMs,
                Underruns = trial.Underruns,
                ShortReadFrames = trial.ShortReadFrames,
                SkippedFrames = trial.SkippedFrames,
                Clean = trial.Clean,
            }).ToList(),
        };
    }
}

