# Ruse handoff

## Snapshot

- Version **0.8.2**, branch `main`
- Repo: `C:\Users\ih_me\Projects\Ruse`
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Package: `C:\Users\ih_me\Projects\Ruse-package\Ruse-0.8.2-win-x64.zip`
- Runtime data: `%APPDATA%\Ruse` (settings, session, chains, templates, `control.json`, `ruse.log`, models)

## Architecture

- `Ruse.Engine` - audio core. `GraphEngine` renders the graph once; captures and output pumps are separate threads.
- `Ruse.App` - WPF/WebView2 host, bridge, persistence, control API, speech/translation/TTS.
- `Ruse.App/WebUi` - React + React Flow UI.
- Graph DSP is 48 kHz stereo float. VST3 is isolated by default.
- Native passthrough bypasses the graph and runs a whitelist of native-safe processors/taps at the source rate.

## Current state

- **Effects:** 61 built-ins, including the clean-room Moog, auto-wah, frequency shifter, vocoder, string resonator, formant filter, reverse delay, waveset and phase distortion.
- **Native passthrough:** direct source-to-device/recorder cable. Native-safe chain: gain/pan, polarity, mid/side, phase distortion, clipper; taps: waveform, spectrum, spectrogram, dB meter. File recorder rates: 44.1/48/96 kHz (MP3 44.1/48).
- **Pitch:** selectable autocorrelation/AMDF tracker. Auto-tune analysis runs off the render thread.
- **Performance:** leased graph snapshots; bounded control API outbox; catalog/app diffing; Core Audio device notifications; true-peak lookahead clip guard; denormal flushing and block-rate smoothing.
- **Visualizers:** 256-band spectrum/spectrogram, triggered waveform, dB/loudness taps. Spectrogram labels have dark backing bars.
- **UI/API:** live `param`, `node-state` and `link-state` deltas; structural graph events are reliable and resyncable; library/mixer memoized. The control API is off by default and can be enabled in Settings > Behavior.
- **Updates:** Settings > Info checks the latest GitHub release manually or automatically every 1/7/30 days, verifies the downloaded ZIP with its published SHA-256, stages it, and swaps it with a backup/rollback helper. Settings can also create a desktop shortcut. The first build with this feature must be installed manually.

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
cd src\Ruse.App\WebUi && npm test
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1
```

Useful modes: `--web-test`, `--dsp-test`, `--effects-test`, `--selftest`, `--control-test`, `--demo`, `--live-demo --pid <pid>`. Close Ruse before building.

Last verified: clean build, **17/17 regression**, 61 effects, 24 DSP checks, native passthrough/recorder checks, UI typecheck/locales.

## Known / next

- ASIO still needs real loopback/insert testing.
- Engine rate is fixed at 48 kHz. Source resampling now tracks the measured output clock; per-device output SRC for rate-converted endpoints remains.
- VST3 editor state/parameter feedback, sidechain buses and MIDI remain incomplete.
- Control API has no CORS policy or rate limiting.
- Some native status/history/error text remains English.
