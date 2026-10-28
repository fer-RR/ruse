# Backlog - ideas and decisions

Nothing here is started unless noted. Keep this list short and current.

## Before new feature work

- Commit the uncommitted transcriber/settings/UI overhaul (tap monitor,
  resampler, shared model cache, model manager, tabbed Settings, cable
  animation, diagnostics node, collapsible library, Graphic EQ, dB meter).
- Run the full regression and `--web-test` after committing.

## Transcriber

- Add cryptographic checksums for downloaded models (installs are currently
  validated by file size, not a published hash).
- Add a free-disk/RAM preflight before downloading large models (they can be
  1-4.4 GB on disk and need up to 16 GB of RAM at runtime).
- Move the Whisper processor itself into the child worker (not just the load
  probe) so a native crash during recognition cannot take down the UI.
- Add timestamped segments and .srt export; plain .txt export is done.
- Add a compact transcript font-size / layout option for long sessions.
- Test real microphones; synthesized US English and Brazilian Portuguese are
  verified over system loopback.
- Consider optional punctuation/capitalization post-processing for Whisper.

## UI languages

- Add more locale files (Spanish, French, etc.) and a coverage check.
- Translate native status/history text, device/model names and engine errors;
  this needs key-based messages from the controller instead of English strings.
- Add RTL layout support if an RTL language is added.
- Consider language auto-detection from Windows on first run.

## App follow and library

- Save per-app route templates so an app can be auto-added and connected even
  after its source node was removed from the canvas.
- Let the user pick which active instance to follow when an app has several
  processes.
- Add a short library onboarding highlight for the Sources/Destinations tabs
  and the device guide.

## Onboarding and duplicate audio

- Add an optional "set up a virtual cable for me" helper (detect VB-Audio /
  Voicemeeter, explain the default-device change, add the matching source).
- The current warning can be dismissed for the session. Add a permanent
  "intentional monitoring" flag per route so its duplicate warning stays hidden
  across restarts.
- Test duplicate detection with real apps that have sessions on several
  devices, and with apps that move between devices while routed.
- Run the welcome wizard against a brand-new Windows user profile.

## Audio engine

- Per-node output device switching without deleting/re-adding the node.
- VST3/CLAP hosting. Needs a native C++ host, Steinberg SDK acceptance, IPC,
  and an HWND plugin editor. Not started.
- Sidechain and MIDI-trigger support.
- Add a dedicated Graphic EQ curve view/spectrum (the engine uses NAudio's
  10-band `GraphicEqualizer`; the UI currently exposes its 10 gains as sliders).
- A mixer view beside the node graph (channel strips with fader/pan/mute).
- Optional GPU inference for Whisper if a chunked, higher-accuracy mode is
  added later. Current real-time node uses Whisper.net on CPU.

## UI / platform

- Re-check the WPF render tier under Settings > Info; it reported tier 2
  in the latest session, so the old tier-0 investigation may be stale.
- Decide whether to keep the legacy WPF shell long-term or reduce it to a
  fallback/test harness.
- Add a compact "what changed" or release note surface for presets/models.
- Extend the diagnostics node with per-thread audio health and a device/process
  tree; consider saving its placement in presets instead of localStorage.
