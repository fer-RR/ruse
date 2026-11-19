# Ruse 0.9.17

This release adds the audio-engine latency pass and makes stale device nodes
safe to save, visible, and easy to remove.

## Audio engine

- MMCSS now applies thread priorities: graph/render output threads run critical,
  capture and worker threads run high.
- Low-latency mode renders the graph directly in the WASAPI event callback,
  removing the output pump and extra wave-buffer stage for non-native outputs.
- Added opt-in exclusive output with automatic shared-mode fallback.
- Device capture and system loopback request the Pro Audio MMCSS class and
  report the actual WASAPI capture latency.
- Process-loopback capture queries the minimum device period before falling
  back to the default.
- `ClipGuard` is sample-rate aware, runs in the native output path, and reports
  its added latency.
- Native paths detect source drift and engage the variable-rate drift resampler.
- Native passthrough defaults on, auto-enables for eligible direct routes, and
  keeps a per-cable manual opt-out in presets.
- Source and output telemetry carries endpoint/source rates, resampling flags
  and converter latency; node badges show SRC rate changes.
- Output latency penalties recover gradually after a stable period, and
  underrun logging no longer runs on the render thread.

## Invalid nodes

- Saving a session, preset, group or frame template now skips nodes whose
  device or file is no longer valid. Links that reference a skipped node are
  omitted with it instead of breaking the save.
- Nodes that are invalid in the live graph show a red title and the same
  dimmed/grayscale treatment as a bypassed node. Hovering the title shows the
  reason, such as an unavailable output device.
- Device hot-plug and driver changes republish the graph so invalid state
  updates without restarting Ruse.
- Non-finite effect parameters are skipped during serialization, and preset
  JSON tolerates legacy non-finite values. A single bad parameter can no longer
  make the whole session fail to save.

## Validation

- Debug and Release builds: 0 warnings.
- UI tests: 35 passed.
- Regression suite: 18 passed, including the new invalid-node save check.
- Effects test: 63 effects.
- DSP self-test: 36 checks.
- Packaged `--web-test`: PASS.
