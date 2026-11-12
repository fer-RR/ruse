# Backlog

Keep this short. Nothing is started unless noted.

## Before next feature work

- Run live translation/OPUS-MT tests, review `--demo` screenshots, and test
  translator/VST3 nodes with real microphones, noisy routes and the installed
  `ring-out`, `rnnoise` and `DirtyFilterPlus` plugins.
- Sync `work` when the user asks.

## DSP

- Phase 0 guardrails are in place: `--dsp-test` checks primitives and
  `--effects-test` enforces per-effect CPU budgets.
- Phase 1 is in: Moog ladder, auto-wah, frequency shifter, bandlimited test
  tone with pink/brown noise, and an RMS sidechain detector.
- Phase 2 is in: 16-band sidechain vocoder and string resonator.
- Phase 3 is in: formant/vowel filter, reverse delay, waveset, phase
  distortion, phaser vibrato, and a selectable AMDF pitch tracker.
- Remaining optional item: A/B Zita/ReverbSC against the current FDN reverb.
  Reimplement the math in C#; do not copy Soundpipe/Csound/FAUST source.

## Performance follow-ups

- ASIO changes still require a manual refresh; WASAPI device changes use Core
  Audio notifications now.

## Visualizers

- Spectrum and spectrogram now use 256 log bands from the 4096-point FFT, with
  peak-bin levels and a -100..-30 dB display default.
- Waveform tap shows a triggered 256-sample window (5.3 ms) with peak dBFS and
  clip markers. Optional: hold, a longer window, or a per-channel view.
- Spectrogram is incremental with 256 columns (~8.2 s). Consider 20 Hz
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
  Block copies are small at 120-240 frames, so this is an optimization, not a
  correctness fix.
- Capture editor-only state before a watchdog kill. The isolated proxy now
  caches a state snapshot every ~2 s while the editor is open; a true last-
  instant capture before a hung host is killed still needs a safe nonblocking
  channel.
- Editor parameter changes are polled into node sliders while the editor is
  open; editor-only state is cached about every 2 s during the same poll.
- Optional 32-bit bridge. VST2 stays unsupported. CLAP is a future option.
- VST3 sidechain buses and VST3 MIDI. MIDI currently maps effect parameters
  only; node gain/mute/solo, transport and MIDI clock remain. The host protocol
  carries audio blocks only, so VST3 MIDI needs an event transport first.

## Audio and nodes

- Mixer color groups are visual; true group buses with one fader/mute/solo
  remain.
- Graphic EQ: presets, per-band reset, optional live spectrum behind curve.
- Frame templates: rename/delete/thumbnail and refresh after external edits.
- Per-device asynchronous SRC with clock-drift correction. The engine is fixed
  at 48 kHz; ASIO and rate-converted WASAPI endpoints resample at the output.
  Converter drift is tested offline; real-device clock soak remains.
- Persist native-chain link flags across sessions and presets.
- ASIO testing with an insert patch or real loopback interface.
- A 2.5 ms low-latency mode with a larger safety ring is available behind an
  experimental toggle; long soak tests, real-device validation and 128-frame
  device testing remain.
- Realtime residuals: `GraphEngine` no longer disposes its wake event if the
  render thread misses the join, and `OutputNode.Dispose` keeps resources when
  its pump is still alive. `NetworkPcmCapture` can still abandon a blocked
  thread; a job object or watchdog is the next step if a real driver/device
  reproduces it. ASIO capture now preallocates for `FramesPerBuffer` and
  truncates if a driver exceeds it.
- `SourceNode` decode scratch can still grow for capture packets above 16384
  samples; verify large 96 kHz/multichannel devices.
- `AudioFileRecorder.Stop` joins the writer for 2 s; on a failing disk it can
  time out. Keep the writer alive and surface the error rather than disposing
  from another thread.

## App, onboarding and platform

- Per-app route templates so a removed source can return.
- Pick which instance to follow when several processes share a name.
- Permanent intentional-monitoring flag per route.
- Better virtual-cable setup helper.
- Tray: start-minimized and live icon updates.
- Re-check WebView render tier in Settings > Info.
- Release notes and richer thread/device diagnostics.
