# Ruse 0.9.8

This release turns the timeline into a more complete generator and arrangement workspace.

## Timeline

- Resizable timeline dock with persisted height, resize clamping, and double-click reset.
- Clip-edge and playhead snapping while moving, resizing, and time-stretching clips.
- Crossfade action and visible **XFADE** region for overlapping clips.
- Metronome transport click with a selectable 2/3/4/6-beat count-in.
- Per-track level meters with peak hold and clip indication.
- Clear **No source** labels for tracks without a source node.

## Generators

- Added a visible **Metronome** node with tempo and beats-per-bar controls.
- Tone, synth, and metronome nodes can be dragged from their node grip onto the timeline.
- Generator tracks are live: their parameters can be automated from timeline automation lanes.
- Generator clips have a default 4-second duration, editable in milliseconds or by dragging the right edge.
- Generator source parameters now appear in the timeline automation target menu.
- Generator tracks are included in offline bounce and freeze, with their source automation applied.

## Fixes

- Generator clips cannot be moved onto normal audio tracks.
- Generator source automation updates the node readouts during playback.
- Bounce/freeze reports progress and supports cancellation.
- Generator node width can be switched between compact and wide layouts.

## Validation

- 18/18 regression tests pass.
- Web self-tests, including generator gating and rendering, pass.
- UI build, locale checks, typecheck, and 5/5 tests pass.
- Clean Debug and Release builds.

Package SHA-256: `0ed513705bb9979b0bc497f816c0ef9e76d86be857f9975f7ec15a134478eb1d`
