using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private List<MixerInputDto> BuildMixerInputs(string mixerId)
    {
        var inputs = new List<MixerInputDto>();
        foreach (var link in _links)
        {
            if (link.To != mixerId)
            {
                continue;
            }

            if (BuildMixerInput(link) is { } input)
            {
                inputs.Add(input);
            }
        }

        return inputs;
    }

    private MixerInputDto? BuildMixerInput(LinkEntry link)
    {
        if (link.Kind == "sidechain" || !_nodes.TryGetValue(link.From, out var from))
        {
            return null;
        }

        return new MixerInputDto
        {
            LinkId = link.Id,
            Title = from.Title,
            Gain = link.Gain,
            Pan = link.Pan,
            DelayMs = link.DelayMs,
            Muted = !link.Enabled,
            Solo = link.Solo,
            BusB = link.BusB,
            Inverted = link.Inverted,
        };
    }

    private void PublishMixerState(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier is not { IsMixer: true } mixer)
        {
            return;
        }

        Post("mixer-state", new MixerStateDto
        {
            Id = entry.Id,
            GraphVersion = _graphVersion,
            Width = mixer.MixerWidth,
            AutoGain = mixer.MixerAutoGain,
            Crossfade = mixer.MixerCrossfade,
        });
    }

    /// <summary>
    /// Sends one mixer input instead of the whole input list. Fader and switch
    /// changes are high-rate, so this keeps the payload O(1) per edit.
    /// </summary>
    private void PublishMixerInputChange(LinkEntry link)
    {
        if (!_nodes.TryGetValue(link.To, out var entry) ||
            entry.Modifier is not { IsMixer: true } ||
            BuildMixerInput(link) is not { } input)
        {
            return;
        }

        Post("mixer-input-state", new MixerInputStateDto
        {
            MixerId = entry.Id,
            GraphVersion = _graphVersion,
            Input = input,
        });
    }
    private void SetLinkGain(string id, float value)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        if (link.Native && Math.Abs(value - 1f) > 1e-4f)
        {
            SetStatus("Disable native passthrough before changing the cable gain.", isError: true);
            return;
        }
        float old = link.Gain;
        bool enabled = link.Enabled;
        ApplyLinkSettings(id, value, enabled, publish: true);
        PushAction("Change send gain",
            undo: () => ApplyLinkSettings(id, old, enabled, publish: true),
            redo: () => ApplyLinkSettings(id, value, enabled, publish: true),
            coalesceKey: $"send-gain:{id}",
            structural: false);
    }

    private void SetLinkPan(string id, float value)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        float old = link.Pan;
        float next = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
        link.Pan = next;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
        PushAction("Change input pan",
            undo: () => SetLinkPanValue(id, old),
            redo: () => SetLinkPanValue(id, next),
            coalesceKey: $"link-pan:{id}",
            structural: false);
    }

    private void SetLinkPanValue(string id, float value)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.Pan = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
    }

    private void SetLinkPolarity(string id, bool inverted)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        bool old = link.Inverted;
        link.Inverted = inverted;
        if (link.Kind == "sidechain")
        {
            ApplyLinkRouting(link, publish: false);
        }

        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
        PushAction(inverted ? "Invert input polarity" : "Restore input polarity",
            undo: () => SetLinkPolarityValue(id, old),
            redo: () => SetLinkPolarityValue(id, inverted),
            structural: false);
    }

    private void SetLinkPolarityValue(string id, bool inverted)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.Inverted = inverted;
        if (link.Kind == "sidechain")
        {
            ApplyLinkRouting(link, publish: false);
        }

        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
    }

    private void SetLinkDelay(string id, float delayMs)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        float old = link.DelayMs;
        float next = float.IsFinite(delayMs)
            ? Math.Clamp(delayMs, 0f, ModifierNode.MixerMaxDelayMs)
            : 0f;
        link.DelayMs = next;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
        _graph.RefreshEngine();
        PushAction("Change input delay",
            undo: () => SetLinkDelayValue(id, old),
            redo: () => SetLinkDelayValue(id, next),
            coalesceKey: $"link-delay:{id}",
            structural: false);
    }

    private void SetLinkDelayValue(string id, float delayMs)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.DelayMs = float.IsFinite(delayMs)
            ? Math.Clamp(delayMs, 0f, ModifierNode.MixerMaxDelayMs)
            : 0f;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
        _graph.RefreshEngine();
    }

    private void ResetMixerMeter(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier is not { IsMixer: true } mixer)
        {
            return;
        }

        mixer.ResetMixerMeter();
        PublishTelemetry(force: true);
    }

    private void SetLinkSolo(string id, bool solo)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        bool old = link.Solo;
        link.Solo = solo;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
        PushAction(solo ? "Solo mixer input" : "Unsolo mixer input",
            undo: () => SetLinkSoloValue(id, old),
            redo: () => SetLinkSoloValue(id, solo),
            structural: false);
    }

    private void SetLinkSoloValue(string id, bool solo)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.Solo = solo;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
    }

    private void SetLinkBus(string id, bool busB)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        bool old = link.BusB;
        link.BusB = busB;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
        PushAction(busB ? "Assign mixer input to B" : "Assign mixer input to A",
            undo: () => SetLinkBusValue(id, old),
            redo: () => SetLinkBusValue(id, busB),
            structural: false);
    }

    private void SetLinkBusValue(string id, bool busB)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.BusB = busB;
        SyncMixerInput(link);
        PublishMixerInputChange(link);
        PublishLinkState(id);
    }

    private void SetMixerSettings(string id, float width, bool autoGain, float crossfade)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier is not { IsMixer: true } mixer)
        {
            return;
        }

        float oldWidth = mixer.MixerWidth;
        bool oldAutoGain = mixer.MixerAutoGain;
        float oldCrossfade = mixer.MixerCrossfade;
        float nextWidth = float.IsFinite(width) ? Math.Clamp(width, 0f, 2f) : 1f;
        float nextCrossfade = float.IsFinite(crossfade) ? Math.Clamp(crossfade, 0f, 1f) : 0f;
        ApplyMixerSettings(id, nextWidth, autoGain, nextCrossfade);
        PushAction("Change mixer",
            undo: () => ApplyMixerSettings(id, oldWidth, oldAutoGain, oldCrossfade),
            redo: () => ApplyMixerSettings(id, nextWidth, autoGain, nextCrossfade),
            coalesceKey: $"mixer-settings:{id}",
            structural: false);
    }

    private void ApplyMixerSettings(string id, float width, bool autoGain, float crossfade)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier is not { IsMixer: true } mixer)
        {
            return;
        }

        mixer.MixerWidth = float.IsFinite(width) ? Math.Clamp(width, 0f, 2f) : 1f;
        mixer.MixerAutoGain = autoGain;
        mixer.MixerCrossfade = float.IsFinite(crossfade) ? Math.Clamp(crossfade, 0f, 1f) : 0f;
        PublishMixerState(id);
        PublishTelemetry(force: true);
    }

    private void SetLinkEnabled(string id, bool enabled)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        if (link.Native && !enabled)
        {
            SetStatus("Disable native passthrough before muting the cable.", isError: true);
            return;
        }
        float gain = link.Gain;
        bool old = link.Enabled;
        ApplyLinkSettings(id, gain, enabled, publish: true);
        PushAction(enabled ? "Enable send" : "Mute send",
            undo: () => ApplyLinkSettings(id, gain, old, publish: true),
            redo: () => ApplyLinkSettings(id, gain, enabled, publish: true),
            structural: false);
    }

    private void ApplyLinkSettings(string id, float gain, bool enabled, bool publish)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.Gain = Math.Clamp(gain, 0f, 1.5f);
        link.Enabled = enabled;
        ApplyLinkRouting(link, publish);
        SyncMixerInput(link);
        PublishMixerInputChange(link);
    }

    private void ApplyLinkRouting(LinkEntry link, bool publish)
    {
        float gain = link.Enabled ? link.Gain : 0f;
        if (_nodes.TryGetValue(link.From, out var from) && _nodes.TryGetValue(link.To, out var to))
        {
            if (link.Kind == "direct" && from.Source != null && to.Output != null)
            {
                _graph.SetRouteGain(from.Source, to.Output, gain);
            }
            else if (link.Kind == "modifier-output" && from.Modifier != null && to.Output != null)
            {
                _graph.SetModifierOutputGain(from.Modifier, to.Output, gain);
            }
            else if (link.Kind == "sidechain")
            {
                SetNoteKey(from, to, link.Enabled);
                if (from.Source != null && to.Modifier != null)
                {
                    _graph.SetSourceSidechainSettings(
                        from.Source, to.Modifier, link.Gain, link.Inverted, link.Enabled);
                }
                else if (from.Modifier != null && to.Modifier != null)
                {
                    _graph.SetModifierSidechainSettings(
                        from.Modifier, to.Modifier, link.Gain, link.Inverted, link.Enabled);
                }
            }
        }

        if (publish) PublishLinkState(link.Id);
    }

    private static void SetNoteKey(NodeEntry from, NodeEntry to, bool enabled)
    {
        if (from.Modifier == null || to.Modifier == null)
        {
            return;
        }

        int tapId = enabled
            ? from.Modifier.Processors.OfType<NoteDetectorProcessor>().FirstOrDefault()?.TapId ?? 0
            : 0;
        if (tapId <= 0)
        {
            return;
        }

        foreach (var tune in to.Modifier.Processors.OfType<AutoTuneProcessor>())
        {
            tune.SetKeyTapId(tapId);
        }

        foreach (var tune in to.Modifier.Processors.OfType<AutoTuneExperimentalProcessor>())
        {
            tune.SetKeyTapId(tapId);
        }
    }

    private void SyncMixerInput(LinkEntry link)
    {
        if (link.Kind == "sidechain" ||
            !_nodes.TryGetValue(link.To, out var to) ||
            to.Modifier is not { IsMixer: true } mixer ||
            !_nodes.TryGetValue(link.From, out var from))
        {
            return;
        }

        var settings = new ModifierNode.MixerInputSettings
        {
            Gain = link.Gain,
            Pan = link.Pan,
            DelayMs = link.DelayMs,
            Muted = !link.Enabled,
            Solo = link.Solo,
            BusB = link.BusB,
            Inverted = link.Inverted,
        };

        if (from.Source != null)
        {
            mixer.MixerSourceInputs[from.Source] = settings;
        }
        else if (from.Modifier != null)
        {
            mixer.MixerModifierInputs[from.Modifier] = settings;
        }

        _graph.UpdateMixerInput(mixer, from.Source, from.Modifier, settings);
    }

    private void RemoveMixerInput(LinkEntry link)
    {
        if (link.Kind == "sidechain" ||
            !_nodes.TryGetValue(link.To, out var to) ||
            to.Modifier is not { IsMixer: true } mixer ||
            !_nodes.TryGetValue(link.From, out var from))
        {
            return;
        }

        if (from.Source != null)
        {
            mixer.MixerSourceInputs.Remove(from.Source);
        }
        else if (from.Modifier != null)
        {
            mixer.MixerModifierInputs.Remove(from.Modifier);
        }
    }

}
