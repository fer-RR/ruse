# Backlog

Nothing here is started unless noted. Keep this file short.

## Before next feature work

- Commit the current uncommitted batch: onboarding/splash, 9 locales, tray
  behavior, file player, sidebar/search refinements, cable performance.
- Run the full regression, `--web-test`, and review `--demo` screenshots.

## Localization

- Native-speaker review for es/fr/de/it/ja/ko/zh-CN; technical terms first.
- Automated check that every `t()` key exists in every locale.
- Translate native status/history text and engine errors (needs message keys).
- RTL support and optional Windows language auto-detection.

## Transcription

- Cryptographic checksums for model files (currently size-validated).
- Free disk/RAM preflight before large model downloads.
- Move recognition into the child worker, not just the load probe.
- Timestamped segments / .srt export; plain text export is done.
- Compact transcript layout option; test real microphones.

## Audio and nodes

- File player deck: seek/position, play/pause, per-node output.
- Dedicated Graphic EQ curve/spectrum (gains are generic sliders today).
- VST3/CLAP hosting; native C++ host + IPC + editor window. Not started.
- Sidechain and MIDI triggers.
- Mixer/channel-strip view beside the node graph.
- Optional GPU Whisper mode for higher-accuracy chunked transcription.

## App follow and onboarding

- Per-app route templates so a removed source can be auto-added again.
- Pick which instance to follow when several processes share a name.
- Permanent “intentional monitoring” flag per route (warning dismiss is session
  only).
- “Set up a virtual cable” helper.
- Test duplicate detection across multiple devices and a new Windows profile.

## Platform / UI

- Tray: start-minimized option and live icon creation when the setting changes.
- Re-check WPF render tier in Settings > Info.
- Decide the long-term fate of the legacy WPF shell.
- Add a compact release-notes/what-changed surface.
- Extend diagnostics node with thread health and a device/process tree.
