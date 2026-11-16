# Ruse 0.9.11

This release adds timeline clip processing and makes mixer and timeline edits
stay responsive.

## Timeline

- Added **Normalize** (target peak -0.1 dBFS), **Reverse**, and **Consolidate**.
- Consolidate renders the selected clips or the loop range to one WAV and adds
  it to a new `Consolidated` track, muted by default to avoid doubled playback.
- Offline clip processing runs off the UI thread and reuses the rendered audio
  instead of reading the new WAV back from disk.
- Consolidate is bounded to the 10 minute clip limit and the project length.
- Generated timeline WAVs are removed when no project state or saved session
  references them.
- Fixed recording-path collisions between concurrent offline renders.

## Mixer

- Mixer fader and switch changes use lightweight `mixer-state` deltas instead
  of rebuilding and republishing the whole graph.
- Mixer updates no longer re-render every node on the canvas.

## Themes

- Removed the Deep Dive theme; saved settings migrate to Midnight.

## Validation

- Debug build passes with no warnings.
- UI tests pass.
- `--web-test` passes, including the new normalize, reverse, and consolidate
  checks.
- `--selftest` passes.
