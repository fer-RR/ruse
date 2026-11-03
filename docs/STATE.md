# Ruse handoff

Keep this file short. New sessions start here.

## Snapshot

Ruse is a Windows node-based audio router: .NET 10, NAudio 3.3, WPF host,
WebView2 + React UI, Whisper.net for speech, LLamaSharp/Qwen and optional
ONNX Runtime OPUS-MT for translation, and OwnAudioVst for VST3. One UI; the old
WPF workspace is gone.

- Canonical repo: `C:\Users\ih_me\Projects\Ruse` (`main`, `2612cfd`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (`work`, HEAD `2612cfd`;
  large uncommitted batch, do not reset or clean).
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe` (0.4.1-beta)
- Package: `C:\Users\ih_me\Projects\Ruse-package\Ruse-0.4.1-beta-win-x64.zip`
  (older build; rebuild after committing the current batch). Self-contained
  win-x64, layout `Ruse\Ruse.exe`, `Ruse\Localization\<culture>`,
  README/LICENSE. Models and VSTs excluded.
- Runtime data: `%APPDATA%\Ruse` (`settings.json`, `session.ruse.json`,
  `chains.json`, `frame-templates.json`, `ruse.log`, `models\`).
- Screenshots: `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots`.

## Current state

- **Audio engine:** 48 kHz stereo float. One `GraphEngine` thread renders every
  source/modifier once; one pump thread per output; one capture thread per
  source. Auto timing is the default: 5 ms blocks, 2-block engine cushion,
  balanced device buffer.
- **Timing controls:** Settings > Audio. Auto on/off. Manual block
  2.5/5/7.5/10 ms; device buffer Low/Balanced/Safe/Manual (2–40 ms); engine
  cushion Auto (2 blocks)/Manual (2–50 ms); optional elastic engine ring
  (starts at one block, grows after underruns, shrinks after 2 s stable).
  Changes restart routing.
- **Audio path:** band-limited 49-tap windowed-sinc SRC, 5.1/7.1 downmix,
  smoothed gain/filter/volume, bypass and transport fades, zero-latency clip
  guard, lock-free SPSC rings, asynchronous recording writer.
- **Mixer:** resizable dock; inserts, master strip, per-send gain/enable and
  Pre/Post chain routing, dB meters, solo/mute/color/rename, horizontal or
  vertical strips, color groups. `Ctrl+M` mixer, `Ctrl+L` library.
- **Nodes/features:** sidechain ducker, MIDI-learn effect parameters, file
  player with per-node output routing, frame templates, graphic EQ, parametric EQ with curve, convolution reverb, loudness/true-peak meter, test tone, parallel send, tempo sync, saved
  chains, VST3, ASIO, WASAPI/system/process sources, recorder WAV16/24/32,
  MP3 and 24-bit FLAC.
- **Diagnostics:** source fill, ring fill, underruns/skips per minute, latency
  mean/stddev, estimated ppm drift, output-first startup, and live click
  monitor. Offline transparency harness and optional external ViSQOL.
- **Security:** imported presets cannot restore recorder paths or arm
  recorders; WebView commands require `https://ui.ruse`; isolated VST pipes are
  current-user-only with handshake; block dimensions checked; model downloads
  SHA-256 verified.
- **GPU:** CPU default; Vulkan option with fallback. A user reported a large
  GPU speedup, but keep CPU default until there is a broader GPU matrix.
- **Localization:** 8 UI locales plus `en`; `npm run check-locales` enforces
  key coverage, duplicate keys and placeholders. Native wording review remains.
- **Recent batch:** mixer master/inserts/sends/dB/vertical layout and strip
  navigation; source/output format badges; diagnostics toolbar toggle;
  settings reorder and persisted collapse state; elastic engine ring;
  modifier sidechain keys; click-free effect bypass; lazy recorder graph
  registration; dead monitor/API paths removed. P1 audit hardening: saved-chain
  IR paths, empty-chain creation, EQ drag/keyboard/MIDI controls, preset value
  clamping, stale MIDI cleanup, recorder restore, diagnostics format fields,
  dynamic locale keys, chain disposal, 24/32-bit PCM, bypass latency alignment,
  and asynchronous isolated-VST audio I/O. P0 audit fixes: modifier sends now
  apply their routed gain, live-demo shutdown leaves the saved session intact,
  loudness telemetry no longer sticks, analysis taps are cleaned on graph reset,
  speech/translation model refcounts release on every early exit, and failed
  output starts reset their running state for retry. P2 hardening: send/link
  changes update gain in place without recompiling, faulted chains stop retrying,
  output pump teardown and engine-ring clears are synchronized, loudness reads
  are cached, diagnostics history samples only active graphs at 4 Hz, keyboard
  shortcuts respect dialogs, and the UI has a Vitest runner. Audio pass:
  extensible IEEE-float capture detection, 63-tap/512-phase SRC, direct
  zero-copy stereo conversion, 20 ms capture buffering, TPDF dither for 16-bit
  recording, and non-blocking engine-ring enqueue during output clears.

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
cd src\Ruse.App\WebUi && npm test
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
powershell -ExecutionPolicy Bypass -File scripts\transparency-test.ps1
```

Close Ruse before building; it locks the debug exe. Package publish moves
satellite resources into `Ruse\Localization`. Last verified this batch: build,
`--web-test`, `--preset-test`, `--sidechain-test` and `--selftest`. The older
`regress-ruse.ps1` pass was **16/16**
(web, self-test, effects, record, chains, fan-out, templates, graphic EQ,
WebView origin, recorder disarming, VST bounds, isolated VST, sidechain,
clip guard, ring, transparency). Live modes: `--demo`,
`--live-demo --pid <pid>`, `--monitor-test --pid <pid> [--seconds N]
[--output-match NAME] [--block-ms N] [--engine-cushion-ms N]`.
Optional ViSQOL is external only; it is not bundled or called at runtime.

## Architecture

- Engine: `src/Ruse.Engine` (no WPF). App/UI: `src/Ruse.App`; one
  `WebShellController` serializes commands and snapshots on the dispatcher.
- `GraphEngine` renders a fixed block through sources -> modifiers -> output
  rings. DSP order is deterministic and single-threaded; endpoint pumps run
  independently so fan-out/taps never process effects twice.
- Taps (dB meter, FFT spectrum/spectrogram, transcriber, translator) read a
  modifier’s post-effect block through worker threads and never enter the mix.
- DSP is 32-bit float. SRC is band-limited windowed-sinc with a precomputed
  kernel; no FFT SRC.
- VST3 is one processor per node, isolated by default in a child host with
  watchdog/restart. VST2 is unsupported.
- 48 kHz is still fixed. Sample rates are converted per source; a 44.1 kHz
  endpoint may be converted again by Windows.

## Latency budget (current)

- Auto: 5 ms graph block; engine queue 10 ms (2 blocks); device buffer
  auto/Balanced (6–13 ms observed); WASAPI endpoint 2 ms; effect chain 0 ms in
  the baseline. Output-side estimate is ~19 ms steady / ~22.7 ms peak.
- Source capture backlog is separate: ~8–15 ms after the output-first fix.
  Approximate full source-to-speaker path: ~28 ms steady, ~38 ms peak.
- 2.5 ms block/cushion 5 ms was tested on the dev machine: 7 underruns in 20 s.
  5 / 7.5 / 10 ms were stable; Auto stays at 5 ms / 10 ms cushion.
- Elastic ring is manual-only: target starts at one block, grows one block per
  underrun up to the engine cushion, and shrinks one block after 2 s stable.
- Clip guard adds no latency. VST/native effects add their reported look-ahead
  through PDC. Isolated VST audio uses an asynchronous worker queue with one block of latency.
- MMCSS Pro Audio is applied to graph, pumps, process/file capture and isolated
  VST audio threads. Device/ASIO callback threads belong to NAudio/driver.

## Known issues / next

- Large uncommitted batch; commit only when the user asks.
- Native status/history text, device/model names and engine errors remain
  English. Native localization review pending.
- Fixed 48 kHz engine and optional true-peak lookahead clip guard are documented
  in `docs/BACKLOG.md`.
- VST editor-only state is not recovered after a watchdog restart; editor
  parameter changes are not polled back to sliders. VST3 sidechain buses and
  VST3 MIDI are not implemented.
- ASIO was not changed in the recent passes; ASIO virtual-driver testing still
  needs an insert patch or a real loopback interface.
- Per-app capture needs Windows 11 / Server 2022; Windows 10 uses system audio
  or a virtual cable.
- Qwen 0.5B is approximate, 1.5B default, 3B slower/RAM-heavy. OPUS-MT is
  CPU-only; no English->Japanese/Korean pair.
- Elastic ring only prevents repeat underruns; the first underrun is still
  audible. It controls the engine ring, not the device buffer.
- Post-chain sends need exactly one terminal effect chain. A shared chain
  shares its send gain across sources.
- GPU backends, real-microphone translator/VST3 testing and CLAP remain in
  `docs/BACKLOG.md`.

## Key files

```text
src/Ruse.Engine/Audio/
  GraphEngine.cs, AudioGraph.cs, AudioEngineConfig.cs
  SourceNode.cs, OutputNode.cs, FloatRingBuffer.cs
  FormatConverter.cs, ClipGuard.cs, SidechainDuckerProcessor.cs
  AudioFileRecorder.cs, AudioFileCapture.cs
  PresetModel.cs, Subgraph.cs, FrameTemplateLibrary.cs
  Vst3Processor.cs, IsolatedVst3Processor.cs, Vst3HostRunner.cs
  Transcription/translation/FFT processors
src/Ruse.App/
  WebShellController*.cs                bridge, graph, persistence, diagnostics
  WebShellController.Audio.cs           audio timing settings
  WebShellController.Midi.cs            MIDI runtime
  WebUi/src/                            React workspace and locales
scripts/
  regress-ruse.ps1, package-ruse.ps1, transparency-test.ps1
```

## Conventions

- Comments explain why, not what. Keep them short. No emoji.
- UI text goes through `useI18n().t('English')`; update all locales.
- Effect ids and parameter names are stable (presets depend on them).
- Keep `bin/`, `obj/`, `node_modules/`, `WebUi/dist/`, models, screenshots and
  package output out of the repo.
- Do not commit or merge unless the user asks.
