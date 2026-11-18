using System;
using System.Collections.Generic;

namespace Ruse.App.Audio;

public sealed record PresetValidationResult(
    Preset Preset,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Validates and repairs a deserialized preset before the live graph is
/// replaced. Null collections, duplicate ids, unknown processor kinds and
/// out-of-range link indices are fixed or dropped here, so <c>ApplyPreset</c>
/// never has to clear the current graph for a preset it cannot apply.
/// </summary>
public static class PresetValidator
{
    public static PresetValidationResult Validate(Preset? preset)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (preset == null)
        {
            errors.Add("The preset is empty.");
            return new PresetValidationResult(new Preset(), errors, warnings);
        }

        preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "Preset" : preset.Name;

        preset.Sources = CleanList(preset.Sources, "source", warnings);
        preset.Outputs = CleanList(preset.Outputs, "output", warnings);
        preset.Modifiers = CleanList(preset.Modifiers, "modifier", warnings);
        preset.Frames = CleanList(preset.Frames, "group", warnings);
        preset.Routes = CleanList(preset.Routes, "route", warnings);
        preset.SourceModifiers = CleanList(preset.SourceModifiers, "source→modifier link", warnings);
        preset.Sidechains = CleanList(preset.Sidechains, "sidechain link", warnings);
        preset.BargeKeys = CleanList(preset.BargeKeys, "barge-in key link", warnings);
        preset.MidiBindings = CleanList(preset.MidiBindings, "MIDI binding", warnings);
        preset.ModifierOutputs = CleanList(preset.ModifierOutputs, "modifier→output link", warnings);
        preset.ModifierLinks = CleanList(preset.ModifierLinks, "modifier→modifier link", warnings);
        preset.MixerInputs = CleanList(preset.MixerInputs, "mixer input", warnings);

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        int repairedSources = 0;
        int rejectedPaths = 0;
        foreach (var source in preset.Sources)
        {
            string id = UniqueId(source.Id, sourceIds);
            string kindText = source.Kind;
            SourceKind kind = SourceKind.Application;
            if (string.IsNullOrWhiteSpace(kindText) ||
                !Enum.TryParse(kindText, ignoreCase: true, out kind) ||
                !Enum.IsDefined(kind))
            {
                kind = SourceKind.Application;
            }

            if (id != source.Id || string.IsNullOrWhiteSpace(source.Title) ||
                kind.ToString() != source.Kind || !double.IsFinite(source.X) || !double.IsFinite(source.Y))
            {
                repairedSources++;
            }

            source.Id = id;
            source.Kind = kind.ToString();
            source.Title = string.IsNullOrWhiteSpace(source.Title) ? kind.ToString() : source.Title;
            source.X = Finite(source.X);
            source.Y = Finite(source.Y);
            source.FileSpeed = Finite(source.FileSpeed, 1f);
            source.ToneFrequency = Finite(source.ToneFrequency, 1000f);
            source.ToneLevelDb = Finite(source.ToneLevelDb, -18f);
            source.MetronomeBpm = Finite(source.MetronomeBpm, 120f);
            source.SynthFrequency = Finite(source.SynthFrequency, 110f);
            source.SynthLevelDb = Finite(source.SynthLevelDb, -18f);
            source.SynthCutoffHz = Finite(source.SynthCutoffHz, 2200f);
            source.SynthLfoRateHz = Finite(source.SynthLfoRateHz, 0.7f);
            source.TtsSpeed = Finite(source.TtsSpeed, 1f);
            source.Volume = Finite(source.Volume, 1f);
            if (!string.IsNullOrWhiteSpace(source.FilePath) && !TrustedPath.IsLocalFile(source.FilePath))
            {
                source.FilePath = null;
                rejectedPaths++;
            }
        }

        if (repairedSources > 0)
        {
            warnings.Add($"Repaired {repairedSources} source entr{(repairedSources == 1 ? "y" : "ies")}.");
        }

        foreach (var output in preset.Outputs)
        {
            output.Title = string.IsNullOrWhiteSpace(output.Title) ? "Output" : output.Title;
            output.DeviceId ??= "";
            output.X = Finite(output.X);
            output.Y = Finite(output.Y);
            output.Volume = Finite(output.Volume, 1f);
            output.RecorderSilenceStopSeconds = Finite(output.RecorderSilenceStopSeconds);
            output.RecorderStartThresholdDb = Finite(output.RecorderStartThresholdDb, -40.0);
            if (!string.IsNullOrWhiteSpace(output.FilePath) && !TrustedPath.IsLocalFile(output.FilePath))
            {
                output.FilePath = null;
                rejectedPaths++;
            }
        }

        if (rejectedPaths > 0)
        {
            warnings.Add($"Rejected {rejectedPaths} unsafe file path(s).");
        }

        var frameIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in preset.Frames)
        {
            frame.Id = UniqueId(frame.Id, frameIds);
            frame.Name = string.IsNullOrWhiteSpace(frame.Name) ? "Group" : frame.Name;
            frame.X = Finite(frame.X);
            frame.Y = Finite(frame.Y);
            frame.Width = Math.Clamp(Finite(frame.Width), 60, 20000);
            frame.Height = Math.Clamp(Finite(frame.Height), 40, 20000);
        }

        int droppedProcessors = 0;
        foreach (var modifier in preset.Modifiers)
        {
            modifier.Title = string.IsNullOrWhiteSpace(modifier.Title) ? "Modifier" : modifier.Title;
            modifier.X = Finite(modifier.X);
            modifier.Y = Finite(modifier.Y);
            modifier.Send = Finite(modifier.Send, 0.5f);
            modifier.MixerWidth = Finite(modifier.MixerWidth, 1f);
            modifier.MixerCrossfade = Finite(modifier.MixerCrossfade, 0f);
            modifier.Processors ??= new List<PresetProcessor>();
            droppedProcessors += CleanProcessors(modifier.Processors);
        }

        if (droppedProcessors > 0)
        {
            warnings.Add($"Dropped {droppedProcessors} invalid effect(s).");
        }

        int sourceCount = preset.Sources.Count;
        int outputCount = preset.Outputs.Count;
        int modifierCount = preset.Modifiers.Count;
        int droppedLinks = 0;

        droppedLinks += Prune(preset.Routes,
            route => Index(route.SourceIndex, sourceCount) && Index(route.OutputIndex, outputCount));
        droppedLinks += Prune(preset.SourceModifiers,
            link => Index(link.SourceIndex, sourceCount) && Index(link.ModifierIndex, modifierCount));
        droppedLinks += Prune(preset.Sidechains,
            link => Index(link.ModifierIndex, modifierCount) &&
                    (Index(link.SourceIndex, sourceCount) ||
                     link.SourceModifierIndex is int key && Index(key, modifierCount)));
        droppedLinks += Prune(preset.BargeKeys,
            link => Index(link.SourceIndex, sourceCount) && Index(link.ModifierIndex, modifierCount));
        droppedLinks += Prune(preset.ModifierOutputs,
            link => Index(link.ModifierIndex, modifierCount) && Index(link.OutputIndex, outputCount));
        droppedLinks += Prune(preset.ModifierLinks,
            link => Index(link.FromModifierIndex, modifierCount) &&
                    Index(link.ToModifierIndex, modifierCount));
        droppedLinks += Prune(preset.MixerInputs,
            input => Index(input.ModifierIndex, modifierCount) &&
                     (Index(input.SourceIndex, sourceCount) ||
                      Index(input.InputModifierIndex, modifierCount)));
        droppedLinks += Prune(preset.MidiBindings,
            binding => Index(binding.ModifierIndex, modifierCount) &&
                       Index(binding.ProcessorIndex, preset.Modifiers[binding.ModifierIndex].Processors.Count));

        if (droppedLinks > 0)
        {
            warnings.Add($"Dropped {droppedLinks} invalid link or mapping entr{(droppedLinks == 1 ? "y" : "ies")}.");
        }

        foreach (var source in preset.Sources)
        {
            if (source.TtsSourceModifierIndex >= 0 && !Index(source.TtsSourceModifierIndex, modifierCount))
            {
                source.TtsSourceModifierIndex = -1;
                warnings.Add($"Cleared a TTS source reference on '{source.Title}'.");
            }
        }

        return new PresetValidationResult(preset, errors, warnings);
    }

    private static List<T> CleanList<T>(List<T>? list, string name, List<string> warnings) where T : class
    {
        if (list == null)
        {
            warnings.Add($"The preset had no {name} list.");
            return new List<T>();
        }

        int removed = list.RemoveAll(item => item == null);
        if (removed > 0)
        {
            warnings.Add($"Dropped {removed} null {name} entr{(removed == 1 ? "y" : "ies")}.");
        }

        return list;
    }

    private static int Prune<T>(List<T> list, Func<T, bool> keep) where T : class =>
        list.RemoveAll(item => item is null || !keep(item));

    private static int CleanProcessors(List<PresetProcessor> processors)
    {
        int dropped = processors.RemoveAll(processor =>
            processor == null ||
            string.IsNullOrWhiteSpace(processor.TypeId) ||
            !IsKnownProcessor(processor.TypeId));
        foreach (var processor in processors)
        {
            processor.Parameters ??= new Dictionary<string, float>();
            if (processor.Elements != null)
            {
                dropped += CleanProcessors(processor.Elements);
            }
        }

        return dropped;
    }

    private static bool IsKnownProcessor(string typeId) =>
        typeId == Vst3Processor.Id ||
        typeId == "chain" ||
        typeId == ParallelChainProcessor.Id ||
        AudioProcessorCatalog.Find(typeId) != null;

    private static bool Index(int index, int count) => index >= 0 && index < count;

    private static string UniqueId(string? id, HashSet<string> used)
    {
        string value = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        if (!used.Add(value))
        {
            do
            {
                value = Guid.NewGuid().ToString("N");
            }
            while (!used.Add(value));
        }

        return value;
    }

    private static double Finite(double value, double fallback = 0) =>
        double.IsFinite(value) ? Math.Clamp(value, -1_000_000, 1_000_000) : fallback;

    private static float Finite(float value, float fallback) =>
        float.IsFinite(value) ? value : fallback;
}
