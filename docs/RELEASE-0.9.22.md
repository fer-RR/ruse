# Ruse 0.9.22

This release focuses on VST3 and MIDI: plugins run more safely, their state can
be saved and restored, and hardware MIDI controllers now drive notes, transport
and the timeline. It also adds drop-to-connect on the canvas and sidechain key
controls.

## Drop a node to connect it

- Dragging a node on top of another node now creates a cable automatically when
  the two can be connected, in either direction.
- Works for sources, effects, outputs and network nodes. Existing cables and
  audio loops are skipped, and multi-node drags are left untouched.
- Dropping a library item onto a node connects it in the same way. The
  connection is part of the same undo step as the new node.

## VST3

- VST3 nodes always run in the isolated host, so a crashing plugin cannot close
  Ruse. The in-process path is kept only for tests.
- Save and load a plugin's full state as a `.rusevstpreset` file from the node.
- The first time an editor opens, it is brought above the Ruse window.
- A restarted plugin host reuses the most recent editor/plugin state instead of
  the state from the initial load.
- Plugins whose bundles contain no runnable binary are filtered out of the
  catalog, and large control messages are now accepted for bigger presets.
- Loading and unloading plugins is serialized to avoid native host conflicts.

## MIDI

- Fixed stuck notes: hardware note-off messages are now parsed correctly, and an
  All Notes Off / All Sound Off panic is sent when a device, channel filter,
  transport or MIDI-enabled state changes.
- Per-node MIDI channel filter and a transport-follow toggle for VST3 nodes.
- MIDI clock updates the timeline tempo and VST3 tempo; Start resets and plays,
  Continue resumes, Stop pauses and keeps the playhead.
- MIDI learn bindings survive node delete/undo and are copied with duplicated
  nodes.
- Hot-plug monitoring reconnects a device when it returns and reports when it
  disconnects.
- Device open/close now runs off the UI thread on a serialized queue, so a slow
  driver cannot freeze the interface. Failed opens retry with a widening backoff.
- `Ruse Test MIDI` now emits a true 120 BPM, 24 PPQN clock.

## Sidechain

- Sidechain key links now have level, polarity and mute controls.
- These settings are saved in presets, restored in undo history, and applied to
  Ruse's managed processors.

## Recorder and canvas

- The recorder's "stop after silence" setting is now a toggle with a duration in
  milliseconds (minimum 100 ms), and it can be turned off.
- Dragged nodes are kept inside the visible canvas.

## Validation

- Release build: 0 warnings, 0 errors.
- Regression suite: 19/19 passed, including new auto-connect, MIDI and VST3
  checks.
- Web UI tests: 62 passed.
- Tested with the isolated host using Dexed, TAL-NoiseMaker and TyrellN6
  instruments.
