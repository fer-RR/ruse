# Backlog

Keep this file short. Nothing here is started unless noted.

## Before next feature work

- Commit the uncommitted batch on `work`.
- Run live translation/OPUS-MT tests, review `--demo` screenshots, and test
  translator/VST3 nodes with real microphones, noisy routes and the installed
  `ring-out`, `rnnoise` and `DirtyFilterPlus` plugins.

## Performance / GPU

- Vulkan is shipped and selectable; CPU remains the default with automatic
  fallback.
- Optional CUDA pack: Whisper CUDA (~240 MB) plus LLamaSharp CUDA (~230 MB).
  Code changes are small; packaging, driver/CUDA support and testing dominate.
- A user reported a large GPU speedup, but results depend on the GPU. Keep CPU
  default until there is a broader per-GPU matrix for Whisper small/medium/large
  and Qwen 0.5B/1.5B/3B.

## Localization

- Native review for es/fr/de/it/ja/ko/zh-CN; technical terms first.
- Translate native status/history text and engine errors.
- RTL support and optional Windows language auto-detection.

## Speech and translation

- Disk/RAM preflight before large downloads; move recognition into the child
  worker.
- Optional glossary/proper-noun list and translation memory.
- OPUS-MT: English to Japanese/Korean models, Qwen quality/latency benchmark,
  per-pair recommendations.
- Source-dependent target-language policy.

## VST3

- Shared-memory audio for the isolated host; current pipes and watchdog work.
- Capture editor-only state before a watchdog kill.
- Poll editor parameter changes back into node sliders.
- Optional 32-bit bridge. VST2 stays unsupported. CLAP is a future option.

## Audio and nodes

- Mixer color groups are visual; true group buses with one fader/mute/solo
  remain.
- Graphic EQ: presets, per-band reset, optional live spectrum behind curve.
- Frame templates: rename/delete/thumbnail and refresh after external edits.
- Per-input/output asynchronous SRC with slow clock-drift correction; telemetry
  now estimates ppm drift.
- Configurable engine sample rate or passthrough to avoid double conversion on
  44.1 kHz endpoints. Current engine is fixed at 48 kHz.
- True-peak lookahead clip guard; current guard is zero-latency sample-peak.
- VST3 sidechain buses and VST3 MIDI. MIDI currently maps effect parameters
  only; node gain/mute/solo, transport, MIDI clock remain.
- ASIO testing with an insert patch or real loopback interface; recent audio
  passes did not change ASIO.
- Revisit 128-frame blocks only with a larger ring/dedicated low-latency mode.

## App, onboarding and platform

- Per-app route templates so a removed source can return.
- Pick which instance to follow when several processes share a name.
- Permanent intentional-monitoring flag per route.
- Better virtual-cable setup helper.
- Tray: start-minimized and live icon updates.
- Re-check WebView render tier in Settings > Info.
- Release notes and richer thread/device diagnostics.
- WASAPI hotplug polls every 2 s; ASIO changes still need manual refresh.
