# Backlog

Nothing here is started unless noted. Keep this file short.

## Before next feature work

- Commit the uncommitted batch on `work`.
- Re-run the regression, `--web-test`, translation tests, VST3 tests and review
  `--demo` screenshots.
- Rebuild the self-contained win-x64 package after committing.
- Test translator and VST3 nodes with real microphones, noisy routes and the
  installed `ring-out`, `rnnoise` and `DirtyFilterPlus` plugins.

## Performance / GPU

- Add optional CUDA/Vulkan builds: `Whisper.net.Runtime.Cuda.Windows` or
  `.Vulkan` for speech, `LLamaSharp.Backend.Cuda12/Vulkan.Windows` for
  translation, with `GpuLayerCount` no longer forced to 0.
- Keep CPU as the default package; expose GPU as an optional build or runtime
  selection. Document driver requirements and GPU memory.
- Measure GPU gain and memory for Whisper small/medium/large and Qwen 0.5B/1.5B/
  3B before making it a default.

## Localization

- Native review for es/fr/de/it/ja/ko/zh-CN; technical terms first.
- Automated check that every `t()` key exists in every locale.
- Translate native status/history text and engine errors (needs message keys).
- RTL support and optional Windows language auto-detection.

## Speech and translation

- Cryptographic checksums for model files (currently size-validated).
- Disk/RAM preflight before large downloads; move recognition into the child
  worker, not only the load probe.
- VAD/sentence segmentation, per-segment timestamps, SRT/VTT export and plain
  text export from the translator.
- Optional glossary/proper-noun list and translation memory.
- Compare Qwen2.5 with a dedicated MT model (M2M-100/OPUS-MT); keep the provider
  boundary clean.
- Language auto-detection for the target when the user does not choose one.

## VST3

- Out-of-process plugin host with shared-memory audio; one crashing plugin must
  not kill Ruse.
- Per-route VST3 instances for fan-out independence (currently one shared
  instance per node).
- Poll VST editor parameter changes back into node sliders, or use the host
  parameter-change callback if the wrapper exposes it.
- 32-bit plugin bridge only if users need legacy 32-bit effects.
- VST2 remains out of scope; Steinberg no longer licenses new VST2 hosts.
- CLAP is a possible future format (open SDK), not started.

## Audio and nodes

- File player deck: seek/position, play/pause, per-node output.
- Dedicated Graphic EQ curve/spectrum (gains are generic sliders today).
- Sidechain and MIDI triggers.
- Mixer/channel-strip view beside the node graph.

## App, onboarding and platform

- Per-app route templates so a removed source can be auto-added again.
- Pick which instance to follow when several processes share a name.
- Permanent “intentional monitoring” flag per route (warning dismiss is session
  only).
- “Set up a virtual cable” helper.
- Tray: start-minimized option and live icon creation when the setting changes.
- Re-check WPF render tier in Settings > Info.
- Add release notes and extend diagnostics with thread health/device tree.
