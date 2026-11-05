# Backlog

Keep this short. Nothing is started unless noted.

## Before next feature work

- Run live translation/OPUS-MT tests, review `--demo` screenshots, and test
  translator/VST3 nodes with real microphones, noisy routes and the installed
  `ring-out`, `rnnoise` and `DirtyFilterPlus` plugins.
- Sync `work` when the user asks.

## Visualizers

- Optional: raise spectrum bands from 128 to 192/256. The 4096-point FFT
  supports it; payload is small.
- Optional: add a time-domain waveform/oscilloscope tap and view. Useful for
  transients, DC and clipping detail; ~512 floats per update.
- Spectrogram is now incremental with 256 columns (~8.2 s). Consider 20 Hz
  telemetry or RAF column scrolling only if smoother motion is needed.
- Avoid decorative modes (particles, glitch, `shadowBlur`, `getImageData`
  scrolling) unless the UI cost is justified.

## Control API

- Optional local CORS policy for browser dashboards; native/server clients
  already work.
- Rate limiting and an audit log for external control.
- UI editor for persisted `ControlId` aliases; currently auto-generated and
  settable via `PATCH /api/v1/nodes/{id}`.
- Optional read endpoints for links/frames; current API is node/parameter/
  transport focused.

## Noise suppression

- Calibrate the static profile against the live noise level so a quiet
  recording does not under-remove.
- Optional per-channel profiles, profile preview, and background analysis.
- Current design: adaptive NAudio suppressor plus a static 257-tap FIR built
  from the loaded noise-floor profile.

## Performance / GPU

- Vulkan is shipped and selectable; CPU remains the default with automatic
  fallback.
- Optional CUDA pack: Whisper CUDA (~240 MB) plus LLamaSharp CUDA (~230 MB).
  Packaging and driver/CUDA testing dominate.
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
- VST3 sidechain buses and VST3 MIDI. MIDI currently maps effect parameters
  only; node gain/mute/solo, transport and MIDI clock remain.

## Audio and nodes

- Mixer color groups are visual; true group buses with one fader/mute/solo
  remain.
- Graphic EQ: presets, per-band reset, optional live spectrum behind curve.
- Frame templates: rename/delete/thumbnail and refresh after external edits.
- Per-input/output asynchronous SRC with slow clock-drift correction.
- Configurable engine sample rate or passthrough to avoid double conversion on
  44.1 kHz endpoints. Current engine is fixed at 48 kHz.
- True-peak lookahead clip guard; current guard is zero-latency sample-peak.
- ASIO testing with an insert patch or real loopback interface.
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
