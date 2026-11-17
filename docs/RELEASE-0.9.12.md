# Ruse 0.9.12

This release focuses on performance, reliability and interface polish.

## Performance and architecture

- Mixer fader and switch changes now send one `mixer-input-state` delta instead
  of rebuilding and serializing the full mixer input list.
- Telemetry reuses its DTOs, dictionaries and cycle sets; the link index is
  cached and invalidated only on structural changes.
- Split large files into focused modules: `WebShellController.Mixer.cs`,
  `bridgeUpdates.ts`, `timelineCanvas.ts` and `TimelineTrackControls.tsx`.
- The timeline canvas indexes automation targets once per draw.

## Reliability

- `NetworkPcmCapture` now polls non-blocking, disposes the socket before joining
  and slices the pump wait, so Stop/Dispose no longer abandon the socket when a
  thread is slow.
- Added tests for link-index reuse/invalidation, telemetry DTO reset, network
  start/stop and the mixer-input delta contract.

## Timeline

- Added vertical zoom: **Alt + wheel** over the timeline, or the **Track height**
  selector (50–200%). Track and automation row heights scale and persist.
- Timeline panning (middle mouse or Alt + drag) now scrolls both horizontally
  and vertically.
- Extracted the canvas renderer and added renderer tests.

## Interface

- Added **Settings > Interface > Interface scale** (80–150%), backed by WebView2
  `ZoomFactor` and persisted across restarts.
- Added the **Receiver** theme: walnut cabinet, brushed aluminum faceplates,
  black glass dials, teal/amber accents and pilot red.
- Hardened layouts for long translations: toolbar, context menus, settings rows,
  model cards, wizard steps, node badges and modal actions.
- Added hairline separators between settings items.
- Fixed the clipped “Find best audio settings” button and the corrupted
  timeline close/remove glyphs.
- Translation pass for German, Spanish, French, Italian, Japanese, Korean,
  Portuguese (Brazil) and Chinese (Simplified), including TTS and network
  strings.

## Validation

- Debug build passes with no warnings.
- UI tests: 17/17.
- `--web-test` passes.
- Full regression: 18/18.
