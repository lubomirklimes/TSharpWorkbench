# TSharpWorkbench.Diagnostics.DriverOutput — driver output qualification (Layer B)

Qualifies what the real drivers do with logical cells and native input, below the logical
screen that TestWorkbench captures.

| Layer | What | Where |
|---|---|---|
| A | Logical `TScreen` cells (TVS1) | TestWorkbench, logical baselines |
| **B** | **Driver translation: cells → native output, native input → `TEvent`** | **this tool** |
| C | Native pixels / terminal or console screenshots | SDL renderers: `TSharpWorkbench.Diagnostics.NativePixels`; console/terminal screenshots not covered |

A Layer B match says nothing about Layer C (fonts, palettes, the terminal emulator itself).

## Paths and observation points

| Path id | Real code run | Observed at |
|---|---|---|
| `console-win32` | `Win32ConsoleDriver` (WriteConsoleOutputW, cursor calls) | a private console screen buffer read back with `ReadConsoleOutputW`, inside a child under `conhost.exe --headless --width W --height H` (never the caller's console) |
| `terminal-ansi` | `AnsiTerminalDriver` output methods | the UTF-8 bytes it writes to `Console.Out`, interpreted by the independent `VtOracle` |
| `terminal-ansi` PTY smoke (Linux) | `AnsiTerminalDriver.Initialize/WriteBuf/Shutdown` on a real PTY (`script`) | the raw PTY bytes, same oracle |
| `sdl-renderer` | `SDLDriver` + `SDLRenderer` on SDL's offscreen video driver, software renderer | `SDLRenderer.Trace` (internal), recorded beside each `SDL_RenderFillRect`/`SDL_RenderTexture` |
| `sdl-gpu-shader` | `TerminalGpuPipeline.BuildVertices` with the real `SDLGpuRenderer` glyph source | the background/glyph vertex streams, decoded back to cells (no GPU device) |
| `sdl-gpu-composite` | `SDLGpuRenderer.CompositeScreen` (CPU fallback) | the CPU frame buffer, checked per cell against the renderer's glyph masks |

The drivers expose only `internal` seams to this assembly (`InternalsVisibleTo`); they do not
reference this project or `TSharpWorkbench.Testing`.

Input parity (`InputParity.cs`) feeds each case as the platform's native form — KEY/MOUSE record
fields, terminal bytes (legacy and kitty), SDL events (`SDL_PushEvent` into the attached offscreen
driver for mouse) — through the drivers' own translators and compares with one expectation
written from the framework constants.

## Running

```
dotnet run --project src/TSharpWorkbench.Diagnostics.DriverOutput -- run
    [--output <dir>]                 default: artifacts/driver-output
    [--grids 80x25,140x40,20x6]
    [--paths console-win32,terminal-ansi,sdl-renderer,sdl-gpu-shader,sdl-gpu-composite]
    [--tvs1 <file-or-dir>]           extra fixtures from TVS1 screens, read only (e.g. approved logical baselines)
    [--no-parity] [--pty]            --pty is on by default on Linux
```

Exit code 0 = every available path matched and parity has no mismatch; 1 = mismatch; 2 = harness
error. A path that cannot run (platform, missing native library) is reported as
`PlatformUnavailable`, never as passed.

Artifacts are flat: `<path>--<fixture>-output--<grid>.json` (per-step capture, comparison,
bounded operation list, text/fg/bg rows), `driver-comparison--<fixture>--<grid>.json`,
`expected--<fixture>-final--<grid>.tvs1`, `input-parity.json`, `run.json`, `report.md`.

The xUnit project `tests/TSharpWorkbench.Diagnostics.DriverOutput.Tests` runs the same code as tests.

## Known platform requirements

- `console-win32`: Windows with `conhost.exe` (headless mode).
- SDL paths: SDL3-CS 3.4.10.2 Linux natives need glibc ≥ 2.38 (Ubuntu 22.04 has 2.35); SDL_ttf
  needs a monospace font found by `SdlFontLocator`.
- PTY smoke: Linux with util-linux `script`.
