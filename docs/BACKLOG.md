# Backlog

Keep this short. Nothing is started unless noted.

## Processing Sound review (2026-11-18)

- [x] Reviewed all six Processing Sound effects and JSyn sources. No
  efficiency port needed; Ruse's delay/Freeverb already exceed them.
- [x] Added the two missing filters: **Band-pass** (`bandpass`) and
  **All-pass** (`allpass`), both on the shared biquad path. Catalog is 63.
  DSP and effects tests cover both.
- [x] Added **Settings > Appearance > Motion** with Follow Windows / Reduce
  motion / Full motion, overriding the Windows preference in either direction.
- [x] Checked Processing Sound `BeatDetector` and `PitchDetector`. BeatDetector
  is a boolean energy-spike detector with no BPM output and no Ruse consumer;
  Ruse's NoteDetector is already stronger than Processing's PitchDetector, so
  neither was ported.

## Audit handoff — 0.9.14 (2026-11-17)

Full findings: `docs/AUDIT-2026-11-17.md`. Fix one at a time, P0 first.

### Manual pass — 2026-11-17

All other P0 checks passed. Two findings fixed:

- [x] Hearing profile amplified residual hiss when the source was silent.
  Root cause: per-app loopback requested 16-bit PCM, and Windows' dither floor
  (~-90 dBFS) was boosted by the hearing EQ/gain. Capture now tries 32-bit PCM
  first (silent test = exact zero) with 16-bit fallback. Added an optional
  noise gate (default off; `-60 dBFS`, 5 ms attack, 250 ms release).
- [x] Preset save/load directory drifted to the last browsed folder. User
  content now lives under `Documents\Ruse` (`Presets`, `Recordings`, `Samples`,
  `Exports`); preset dialogs always start in `Presets`, and Settings > Info
  shows/opens the user folder.

### P0 fix order

Status: all 13 steps implemented (2026-11-17). Gates passed; manual UI-scale
and real-device review still pending. Audit item numbers in brackets.

1. [x] [1] Validate preset/timeline paths before any open (shared trusted-path
   helper; guard `SourceNode` construction and `TimelineService.LoadClip`).
2. [x] [2] Validate a preset before `ClearGraph`/`ClearHistory`; sanitize
   null/indices/kinds and apply only after success.
3. [x] [3] Atomic settings/session/chains/templates writes (`.tmp` + replace +
   `.bak`, surfaced errors).
   - **Gate:** 0-warning build, `--preset-test`, `--selftest`, UI tests,
     full regression. Passed.
4. [x] [4] `OutputNode.Start`: all fallible setup inside try; `_running` only
   after success.
5. [x] [6] `Route` subscription lifetime: no lazy recreation after dispose.
6. [x] [5] Output/native pump fault containment: log, silence the block, keep
   the process alive.
   - **Gate:** fanout/record/chain/align tests, live graph test, targeted
     self-tests. Passed.
7. [x] [9] TTS provider cancellation/disposal (Kokoro jobs, Piper process and
   caches).
8. [x] [8] Bound `TtsStreamCapture` and add TTS queue backpressure.
9. [x] [10] One TTS host per text for non-streaming models.
   - **Gate:** `--tts-test` per backend, cancel/delete tests, UI tests. Passed
     (Kokoro streaming; supertonic batch = 3 clauses through one host).
10. [x] [12] Tray prompt X/Escape/backdrop cancels instead of quitting.
11. [x] [13] Confirm active recorders on clear/frame-with-nodes deletion.
12. [x] [7] Global Space/shortcut guard: `defaultPrevented`, buttons/checks,
    `dialog[open]`/shared modal state, active-surface scoping.
13. [x] [11] Responsive shell at minimum window and 80-150% UI scale.
    - **Gate:** full regression, UI tests, manual 80/100/150% and
      minimum-window pass; update `docs/STATE.md`. Automated gates passed;
      manual UI-scale pass pending.

### P1 fix order

Work in this order; security/data loss first, then runtime correctness, then
speech lifecycle, then UI/performance. Audit bullets below remain the full list.

1. Data loss/security:
   - [x] MIDI preset min/max clamped to the parameter range and sorted.
   - [x] Auto-started fixed-path recorders no longer overwrite an existing take.
   - [x] Network outputs in imported presets are skipped instead of silently
     sending to the stored host.
   - [x] Application sources validate the stored PID/name and re-bind by a
     unique matching process or active audio session instead of following a
     reused PID.
   - [x] Update TOCTOU: re-hash the locked package and re-extract a fresh
     verified stage at apply time; the helper rejects non-update source/log
     paths and targets without an existing `Ruse.exe`.
   - [x] Pin/verify OPUS and streaming TTS models: 70 OPUS file hashes are
     pinned, Kokoro verifies the v2.0.0 model, and Piper uses a pinned
     executable archive plus pinned voice ONNX/JSON hashes.
2. Audio correctness and leaks:
   - [x] Hearing profile reports limiter latency only while the limiter is on.
   - [x] Native passthrough outputs no longer create unused graph
     subscriptions; a lagging native endpoint trims backlog above 200 ms.
   - [x] Native passthrough outputs get private copies of ordinary processors
     (taps/shared plugins stay attached), parameters sync from the modifier,
     and retired instances are disposed off the render path.
   - [x] Clock drift uses a 10 s window with a 2 ppm deadband. Sources that
     reach exactly one device use that device's filtered ppm; fan-out outputs
     use a per-output variable-rate interpolator that consumes directly from
     the existing engine ring, so no extra block buffer is added.
   - [x] Mixer input delay updates the live compiled chain; regression now
     changes delay after compile and checks the delayed impulse.
   - [x] Network input restart no longer revives old threads; `IsRunning`
     requires both receiver and pump threads and `SourceNode` reports it.
   - [x] Failed-stop teardown retries in the background; output resources are
     disposed when the pump finally exits.
   - [x] Source disposal is deferred through the engine snapshot even when the
     caller thinks no route changed, so a live render lease cannot read a
     disposed source.
   - [x] Graph rebuild no longer joins tap workers while holding `_sync`;
     old graph teardown runs on the thread pool.
3. Speech lifecycle:
   - [x] VAD/tap reading is decoupled from Whisper/translation inference through
     a bounded segment channel, so a slow model no longer stops tap draining.
   - [x] Session replacement disposes the old reader before starting the new
     one; transcription leaves its CTS alive if a native worker has not exited.
   - [x] Clear increments a session/tap generation, resets VAD origin and drops
     in-flight transcripts/partials.
   - [x] Restore CJK sentence splitting and clauses/tests (no whitespace needed).
   - [x] Dispose translation `StatelessExecutor` when a session ends; OPUS engine
     cache owns its own model lifetimes.
   - [x] Model cache locks, OPUS delete during translation, GPU probes: loads
     and disposals now happen outside the shared lock, OPUS drop takes the
     inference gate, and probes pass `--gpu` with a GPU-aware cache key.
   - [x] Vosk delete cancels its download, disposes the cached model and retries
     file removal after the last recognizer releases it.
   - [x] TTS pipeline test only selects the streaming provider for
     Kokoro/Piper, has cancellation timeouts and rejects empty output.
4. UI/performance:
   - [x] Edge selection is retained and cable Delete works.
   - [x] Graph shortcuts are scoped to the focused surface; Tidy reads the
     current link snapshot instead of a stale render capture.
   - [x] Control API keeps the latest graph snapshot even with no clients, so
     a late WebSocket resync is current.
   - [x] Timeline cut/paste uses a host-side clip snapshot (metadata + decoded
     audio), so cut clips can be pasted after removal and cross-track; verified
     through snapshot → remove → paste in the timeline regression.
   - [x] UI-scale reflow, contrast token, keyboard canvas access and an accessible
      "Group with..." mixer menu; dragging still works.
   - [x] Telemetry global notifications are change-gated and TTS text polling no
     longer copies cue arrays.
   - [x] Group-fader writes are coalesced to one animation frame and host
     group changes publish per-node state instead of rebuilding the graph.
     Timeline playhead moved to a DOM overlay; the canvas redraw is rAF-coalesced
     and skips the playhead, with memoized sticky labels. Reviewed by code path
     and covered by UI/regression tests.

- **P1:** native passthrough ownership/clock/limiter; mixer delay latency;
  tap/inference decoupling; CTS/session/model lifecycle; CJK segmentation;
  model hashes/probes; control resync; updater TOCTOU; edge selection/shortcut
  scope/timeline cut-paste; contrast/keyboard access; group fader/node-state
  performance.
- **P2:** see audit file; includes remaining persistence, UI, i18n, a11y and
  performance items.
- **Not quick fixes:** native inference cancellation, signed updater, full
  screen-reader graph/timeline parity, cross-layer timeline clipboard,
  responsive-layout architecture, Roaming model migration.

## Before next feature work

- Commit and release the post-0.9.11 work as **0.9.12**: performance/architecture
  pass, network stop fix, UI scale, timeline vertical zoom and
  2D panning, translation and layout hardening.
- Manually review the Vesper theme and interface scale (80–150%) on a live
  routed session.
- Test timeline vertical zoom and 2D panning with many tracks and automation
  lanes.
- Test network input stop/restart and long-running capture.
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

- ASIO support is removed for now. WASAPI device changes use Core Audio
  notifications.

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

- Local CORS, a 600-request/minute loopback rate limit, and write-request audit
  logging are in place.
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

- First terminology pass done for de/es/fr/it/ja/ko/pt-BR/zh-CN (UI labels,
  mixer/timeline terms, TTS and network strings).
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

- Shared-memory audio transport for the isolated host is in place; the named
  pipes remain for control/lifetime and the watchdog restart path is the
  fallback.
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

- Mixer color groups now have a separate gain/mute/solo stage that preserves
  member faders. Group insert chains remain.
- Graphic EQ: presets, per-band reset, optional live spectrum behind curve.
- Frame templates: rename/delete/thumbnail and refresh after external edits.
- Per-output asynchronous clock resampling is in: each device output and the
  network receiver gently adjust their ratio to hold the queue. Real-device
  multi-output clock soak remains.
- Persist native-chain link flags across sessions and presets.
- Re-add ASIO only with a native, callback-safe host and real loopback testing.
- A 2.5 ms low-latency mode with a larger safety ring is available behind an
  experimental toggle; long soak tests, real-device validation and 128-frame
  device testing remain.
- Realtime residuals: graph render faults are logged by a deferred
  `AudioFaultLog`; isolated VST3 uses a lock-free slot handoff; output rings
  trim overfill with a crossfade. `NetworkPcmCapture` now polls non-blocking,
  disposes the socket immediately on stop, and slices the pump wait so shutdown
  latency is bounded; a job object remains a last resort only if a real driver
  still wedges a thread. In-process VST3 still runs on the render thread and
  needs a dedicated plugin thread.
- `SourceNode` decode scratch can still grow for capture packets above 16384
  samples; verify large 96 kHz/multichannel devices.
- `AudioFileRecorder.Stop` joins the writer for 2 s; on a failing disk it can
  time out. Keep the writer alive and surface the error rather than disposing
  from another thread.

## App, onboarding and platform

- Per-app route memory is in: removing an app source saves its outgoing
  cables, and Settings > Behavior can restore them when the app is added again.
- Pick which instance to follow when several processes share a name.
- Permanent intentional-monitoring flag per route.
- Better virtual-cable setup helper.
- Tray: start-minimized and live icon updates.
- Re-check WebView render tier in Settings > Info.
- Release notes and richer thread/device diagnostics.
- Noise calibration now has a countdown and cancel; profile preview remains.
- Window restore is clamped to the current monitor work area.
