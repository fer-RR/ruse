# Backlog

Nothing here is started unless noted. Keep this file short.

## Before next feature work

- Commit the uncommitted batch on `work`.
- Re-run the regression, `--web-test`, translation tests, OPUS-MT tests,
  VST3 tests (including `--vst3-isolated-test`) and review `--demo` screenshots.
- Rebuild the self-contained win-x64 package after committing.
- Test translator and VST3 nodes with real microphones, noisy routes and the
  installed `ring-out`, `rnnoise` and `DirtyFilterPlus` plugins.

## Performance / GPU

- Vulkan is shipped and selectable in Settings. CPU remains the default; the
  option falls back automatically when no Vulkan device is available.
- Optional CUDA pack: `Whisper.net.Runtime.Cuda12.Windows` (~240 MB) plus
  `LLamaSharp.Backend.Cuda12.Windows` (~230 MB) compressed; installed native
  footprint is roughly 1 GB+. Code changes are small (backend setting, runtime
  order, `GpuLayerCount`, fallback), but packaging, driver/CUDA-version support
  and testing dominate. Ship it as an optional download or separate build.
- Measure GPU gain and memory for Whisper small/medium/large and Qwen 0.5B/1.5B/
  3B before making GPU the default.

## Localization

- Native review for es/fr/de/it/ja/ko/zh-CN; technical terms first.
- Automated check that every `t()` key exists in every locale.
- Translate native status/history text and engine errors (needs message keys).
- RTL support and optional Windows language auto-detection.

## Speech and translation

- Cryptographic checksums for model files (currently size-validated).
- Disk/RAM preflight before large downloads; move recognition into the child
  worker, not only the load probe.
- Optional glossary/proper-noun list and translation memory.
- OPUS-MT provider is shipped behind Settings > Speech & translation >
  Translation engine (ONNX Runtime, CPU). Remaining: English to Japanese/Korean
  models (Qwen is used instead), a quality/latency benchmark against Qwen, and
  per-pair model recommendations. NLLB 600M remains an option but is
  CC-BY-NC.
- Source language detection is shown on the nodes; the target "Auto (my
  language)" resolves to the UI language. A source-dependent target policy
  remains.

## VST3

- Hardening for the isolated VST3 host: shared-memory audio instead of
  synchronous pipes, auto-restart after a crash, and a hung-plugin timeout.
- Per-route VST3 instances for fan-out independence (currently one shared
  instance per node).
- Poll VST editor parameter changes back into node sliders, or use the host
  parameter-change callback if the wrapper exposes it.
- 32-bit plugin bridge only if users need legacy 32-bit effects.
- VST2 remains out of scope; Steinberg no longer licenses new VST2 hosts.
- CLAP is a possible future format (open SDK), not started.

## Audio and nodes

- File player deck: seek/position, play/pause and duration are done; a dedicated
  per-node output selector remains.
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
