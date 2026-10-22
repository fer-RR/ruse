# Backlog — ideas that need a decision, a licence or a bigger machine

Written during the overnight work session. Nothing here is started; it is the
list of things that are worth doing but that need the owner to choose.

## Plugins (VST3 / CLAP)

NAudio 3.3 dropped VST support entirely (the packages are now only
`NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`, `NAudio.Midi` and
`NAudio.Asio`), so hosting a VST3 plugin needs a **native C++ component**:

- the VST3 SDK from Steinberg (free, but the licence has to be accepted, and
  the SDK may not be redistributed — a product built on it may need the signed
  agreement),
- a small host process that loads the plugin, negotiates buses and exchanges
  audio with Ruse over a pipe,
- plugin-window embedding (`IPlugView` → an HWND parented to a WPF host window).

Toolchain check: Visual Studio Build Tools 2026 are installed (so MSVC is
available), but there is no cmake and no VST3 SDK on the machine.

**Interim, already done:** 28 built-in effects, including a biquad EQ, a
pitch shifter, a transient shaper and comfort noise, which covers most of what
people reach for a plugin for.

## Replacing the layout transform

The canvas is zoomed with a `LayoutTransform`, which forces WPF to re-render
the whole world layer whenever anything inside it changes — that is why the
meters used to cost about 15 % of a core. The meters now refresh ten times a
second and the world is smaller, but the underlying issue remains.

The fix is to scale the canvas contents by hand (positions, sizes, cable
geometry and the zoom anchor all become scale-aware) and use a plain
`RenderTransform` or no transform at all. That is a broad change across
`MainWindow.NodeVisual.cs`, `MainWindow.Connections.cs` and
`MainWindow.View.cs`.

## A custom window frame

The app still uses the standard Windows title bar. A custom chrome (drag to
move, snap layouts, a compact transport strip) would make it look like a
proper audio tool — Reaper, Voicemeeter and Ableton all do this. It is a
visible change, so it should be a deliberate decision.

## Other things worth doing

- **Per-node output device switching** — change a node's device without
  deleting and re-adding it.
- **VST-style sidechain / MIDI-trigger** on the effects.
- **A real mixer view** beside the node graph (channel strips with faders,
  pan and mute per output).
- **Recording the routed output** to a file (the engine already has every
  sample; it only needs a sink).
- **The `Voicemeeter Virtual ASIO` driver needs Voicemeeter running** to have a
  clock. FlexASIO works standalone and would let the ASIO path be verified
  end to end.
- **The WPF render tier is still 0** on this machine: the graphics stack keeps
  WPF in software mode (D3D9, D3D11 and DXGI all work; the driver is clean;
  the October 2026 cumulative update and the .NET 10.0.11/12/13 runtimes were
  all ruled out). The remaining suspects are the NVIDIA driver rollback and the
  cumulative update itself.
