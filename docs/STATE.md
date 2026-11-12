# Ruse handoff

## Snapshot

- Version **0.8.8**, branch `main`
- Repo: `C:\Users\ih_me\Projects\Ruse`
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Package: `C:\Users\ih_me\Projects\Ruse-package\Ruse-0.8.8-win-x64.zip`
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
- **Visualizers:** 256-band spectrum/spectrogram, triggered waveform, dB/loudness taps. The spectrogram now has Classic/Viridis palettes, a floor/ceiling dB legend with contrast-aware mapping, and a time axis. Spectrogram labels have dark backing bars.
- **UI/API:** live `param`, `node-state` and `link-state` deltas; structural graph events are reliable and resyncable; library/mixer memoized. The control API is off by default and can be enabled in Settings > Behavior.
- **UI craft:** spacing/control tokens, reduced-motion and coarse-pointer support, live-region status/alert announcements, dialog focus restore, labeled toolbar groups/node groups, and React Flow node virtualization (`onlyRenderVisibleElements`). Library section actions share one button style and placement. Themes: Dark, Light, Windows (follows the system light/dark setting), Midnight, Ocean, Ember, Blueprint, Deep Dive, Studio '74 and Midnight Study, chosen from a swatch grid in Settings > Workspace. Mixer faders have dB scales, unity reset and fine keyboard steps; the timeline exposes selected-clip gain and clips can be dragged across tracks; background tasks use toasts; failed device starts offer Retry. The WebUi bundle is split into app, locales, Flow, icons and DnD chunks.
- **Timeline render:** Bounce mix and Freeze clone the track and shared master effect chains and render through them offline (2 s tail); VST3 effects are skipped in offline renders.
- **Updates:** Settings > Info checks the latest GitHub release manually or automatically every 1/7/30 days, verifies the downloaded ZIP with its published SHA-256, stages it, and swaps it with a backup/rollback helper. Settings can also create a desktop shortcut. The first build with this feature must be installed manually.

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
cd src\Ruse.App\WebUi && npm test
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1
```

Useful modes: `--web-test`, `--dsp-test`, `--effects-test`, `--selftest`, `--low-latency-test`, `--control-test`, `--demo`, `--live-demo --pid <pid>`. Close Ruse before building.

Last verified: clean build, **18/18 regression**, 61 effects, 24 DSP checks, native passthrough/recorder checks, 2.5 ms profile and converter drift checks, UI typecheck/locales.

## Known / next

- ASIO still needs real loopback/insert testing.
- Engine rate is fixed at 48 kHz. Source resampling tracks the measured output clock; ASIO and rate-converted WASAPI endpoints resample in `AsioFloatProvider`/`WasapiPlayer`. Converter drift correction is covered by `--converter-test`; real-device clock soak remains.
- VST3 editor parameter changes are polled back into node sliders in both in-process and isolated hosts. VST3 sidechain buses and VST3 MIDI remain incomplete.
- Control API has no CORS policy or rate limiting.
- Some native status/history/error text remains English.
