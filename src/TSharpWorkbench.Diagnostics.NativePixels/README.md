# TSharpWorkbench.Diagnostics.NativePixels — native-pixel qualification (Layer C)

Reads back the pixels the real SDL renderers produce and checks them against an independent
raster oracle.

| Layer | What | Where |
|---|---|---|
| A | Logical `TScreen` cells (TVS1) | TestWorkbench, logical baselines |
| B | Driver translation (render calls, vertices, CPU frame) | `TSharpWorkbench.Diagnostics.DriverOutput` |
| **C** | **Pixels actually rendered by the SDL renderers** | **this tool** |

A Layer C match qualifies the captured renderer stage. It says nothing about the OS compositor
or the monitor.

## Paths and capture boundaries

| Path id | Real code run | Pixels read from |
|---|---|---|
| `sdl2d-software` (required) | `SDLDriver` → `SDLRenderer` → SDL_Renderer `software` (offscreen video driver) | `SDL_RenderReadPixels` after the whole frame is drawn, before `SDL_RenderPresent` (`SDLRenderer.BeforePresentForTesting`) |
| `sdl2d-<backend>` | same, on each hardware SDL_Renderer backend SDL lists (`direct3d11`, `direct3d12`, `direct3d`, `opengl`, `opengles2`, `vulkan`), hidden window | same |
| `sdl-gpu-shader` (required) | `SDLGpuDriver` → `SDLGpuRenderer` → `TerminalGpuPipeline` on a real SDL_GPU device (hidden window, render thread) | the frame rendered again by the same production code into a readable texture of the swapchain format, `DownloadFromGPUTexture` + fence (`SDLGpuRenderer.RequestCaptureForTesting`) |
| `sdl-gpu-fallback` (required) | same driver; shader-pipeline construction fails inside its own try block (`SimulateShaderPipelineFailureForTesting`), so the production CPU-compositing fallback is selected | the fallback's upload + clear + 1:1 blit recorded into the readable texture, downloaded |
| `sdl-gpu-shader-vulkan`, `sdl-gpu-fallback-vulkan` | as above with `TSHARPVISION_SDLGPU_BACKEND=vulkan` | as above |

`sdl2d-gpu` (SDL's own `gpu` SDL_Renderer backend) is listed as NotRun: SDL 3.4.10 faults
(0xC0000005) inside `SDL_RenderReadPixels` on that backend.

Swapchain images are never used as a copy source; SDL_GPU does not guarantee that is allowed.

## What is compared

- **Independent raster oracle** (`RasterOracle`): logical cells + VGA palette (`Vga`, from the VGA
  specification) + caret model + glyph coverage (`GlyphMaskOracle`). It shares no rendering code with the drivers.
  Glyph *shapes* come from the same sources the product uses: the font file rasterised by SDL_ttf,
  and the CP437 B0–DF cell generator. Placement, clipping, colour and blending are the oracle's own code.
  Since both back-ends place font glyphs by one rule (`SdlFontCellPlacement`): text on a
  shared baseline (the font baseline moved into spare descent rows only as far as accents need),
  re-rasterised by FreeType when it still does not fit (overflowing axis condensed ≤ 15 %, else
  shrunk); box drawing and block elements U+2500–U+259F on the font baseline, clipped.
- **Rules**: background, foreground and letterbox pixels must match exactly. Partial glyph coverage
  may deviate by at most ±1 per channel (rounding of the blend). Pixels the oracle cannot state
  exactly are counted as *unverified* and never passed (none since native-pixel qualification: SDL 2D no longer
  scales glyph surfaces).
- **Pixel-only geometry checks** (`GeometryChecks`): blank cells, block elements, box-drawing arms and
  same-weight joins, shadow colours, caret bar and colours, glyph ink, per-cell colour range.
- **Anti-clipping check** (`DiacriticChecks`): independent of the placement rule. Every
  cell whose glyph has several ink parts when SDL_ttf draws it unplaced (accents, i, j, ;) must
  show the same parts in the captured pixels, each with at least 60 % of its unplaced share of ink.
- **Update check**: cells that changed between consecutive frames must be exactly the cells whose
  expected rasters changed. This catches stale content and collateral redraws.
- **Negative controls**: twelve deliberate mutations of each path's real capture must all be detected,
  including two clipped-accent simulations (the pre-fix rule's frame; accents cut from the capture).
- **Cross-renderer**: exact comparison of two paths' captures. This is consistency only, not correctness.

Font is pinned to `%WINDIR%\Fonts\consola.ttf` 20 pt (ships with Windows), with the drivers'
symbol fallback (`seguisym.ttf`).

Fixtures: the driver-output composite screen and resize, plus `czech` (`CzechFixture`: Czech upper/lower
case, ASCII, punctuation, boxes, blocks, hotkeys, selection, block/underline caret over accents, resize).

`glyph-audit <out.json>` rasterises a fixed character set through the product's
`SdlCellGlyphRasterizer` (no window) and compares each mask with the oracle and the unplaced glyph.

## Running

```
dotnet run --project src/TSharpWorkbench.Diagnostics.NativePixels -- run
    [--output <dir>]                 default: artifacts/native-pixels
    [--paths id,...]                 default: required paths + every hardware backend SDL lists
    [--commander <dir>]              read-only Commander logical approved TVS1 directory (8 selected screens)
    [--tvs1 <file-or-dir>]           any other TVS1 inputs, read only
    [--png representative|minimal]   which captures are kept as PNG (all steps are compared)
    [--grids 80x25,140x40,20x6] [--timeout <s per path>]
```

Each path runs in its own child process (`child <path> <dir>`), killed after the timeout.
Exit codes: 0 = every required path matched with steps compared, 1 = mismatch, 2 = error,
3 = a required path is unavailable.

Artifacts are written to one flat directory per run:
`<path>--<fixture>-<step>--<grid>.native.png` (actual renderer pixels),
`<path>--<fixture>-<step>--<grid>.diff.png` (EXPECTED | ACTUAL | DIFFERENCE: red = rule violation,
yellow = tolerated partial deviation, blue = unverified),
`<path>--<fixture>--<grid>.pixels.json`, `<path>--path-result.json`, `<path>--child.log`,
`cross--<a>-vs-<b>--<capture>.diff.png`, `baseline-candidates.json`, `run.json`, `report.md`.

## Font fitting

`[graphics] fontFit` (`Condense` default, `Uniform`) is qualified by running every path once per fit
mode and font:

```
dotnet run --project src/TSharpWorkbench.Diagnostics.NativePixels -- run
    --output artifacts/font-fit --font-fit condense,uniform --fonts consolas20,jetbrains20
    --paths sdl2d-software,sdl2d-direct3d11,sdl-gpu-shader,sdl-gpu-fallback,sdl-gpu-shader-vulkan,sdl-gpu-fallback-vulkan
```

- **Configuration.** The parent writes one real .cfg per variant (`<fit>--<font>.cfg`: `[graphics]`
  `fontName`, `fontSize`, `fontFit`). Each child is started with `--config <cfg> --font-id <font>`
  and loads it through `TSharpVisionConfigurationLoader`, the same way an application does. The
  loader resolves the typed `GraphicsDriverConfiguration.FontFit` the drivers read; an
  invalid value fails the load and is reported as the path's unavailability.
- **Programmatic.** `child <path> <dir> --programmatic-fit <Condense|Uniform>` uses no
  .cfg: the child sets `GraphicsDriverConfiguration.FontFit` in code (pinned Consolas 20). Its
  artifacts are prefixed `programmatic-<fit>--consolas20--`; the native-pixel tests compare them
  with the cfg variant of the same mode.
- **Identity.** Each child records the configured mode, the mode the driver resolved and the mode
  the renderer's rasteriser uses, plus the common text size it measured (`fontFit.*` in the path
  identity). A disagreement with the cfg or with the oracle's size is an error.
- **Oracle.** `GlyphMaskOracle(..., uniform: true)` restates the Uniform rule independently:
  - the repertoire is printable ASCII, the Latin-1 letters U+00C0–U+00FF without × and ÷, and Latin
    Extended-A without the compatibility letters Ĳ ĳ Ŀ ŀ ŉ ſ;
  - the size is the largest whole point size, from the configured size down to half of it, at which
    the repertoire's ink fits the cell (its width, and its rows above plus below one baseline);
  - the baseline is the font's line box at that size centred in the cell, clamped to the band the
    ink needs;
  - the pen centres the common advance.

  Glyphs outside the repertoire that still overflow take the Condense fit from the common size. The
  anti-clipping check uses the glyph unplaced at the common size as its reference.
- **Negative controls.** Uniform runs add six, all drawn on the real Czech capture:
  - ASCII at the configured size (the Condense rule);
  - the rejected per-glyph shrink;
  - horizontal-only condensation;
  - the baseline one row up;
  - box drawing at text size;
  - cropped descenders.
- **Cross-mode comparison.** `cross--condense-vs-uniform--<font>--<path>--<capture>.diff.png` is
  written for representative captures. It measures how much Uniform changes a frame; it is not a
  pass/fail check.
- **`font-fit-audit <out.json> [--fonts id,...]`.** This runs without a window, and the run writes
  its output as `font-fit-metrics.json`. It rasterises ASCII, Czech, the rest of the repertoire, the
  compatibility letters, symbols and box drawing through the product rasteriser in both modes. It
  compares each glyph with the oracle of the same mode and records:
  - the common size, the baseline, the pen and the ink extremes;
  - the sizes that were rejected;
  - the time needed to measure the repertoire.

  It also checks that modes do not leak between rasteriser instances, and that the auxiliary font
  handles are released.

Variant artifacts carry the prefix `<fit>--<font>--` (for example
`uniform--consolas20--sdl-gpu-shader--czech-full--80x25.native.png`). Runs without `--font-fit`
keep the native-pixel names and the in-memory configuration.

## Native baselines

None are approved. `baseline-candidates.json` lists the required paths' PNGs with their SHA-256 for
owner review. Each one has status `NoApprovedBaseline`. Logical baselines are input only, never
a pixel reference. Each candidate has a `key` of the form `<fontFit>/<font>/<path>/<capture>/<grid>`
(`default` for the native-pixel run), so a Condense candidate never shares a key with a Uniform one.

The xUnit project `tests/TSharpWorkbench.Diagnostics.NativePixels.Tests` runs the required paths through the
same child mode and tests the comparison machinery on synthetic images.
# Actual Commander application attachment

`ApplicationExecution.cs` is an internal provider for TestWorkbench's audited Windows SDLDriver/software Commander
workflows. It is loaded in the real diagnostic Commander process, initializes the production driver before TCommanderApp,
and captures that driver's own live application buffer through the existing before-present readback seam. It does not
use the standalone fixture runner, replay TVS1, replace the application buffer, or drain framework input during capture.
Keys and supported menu clicks enter the process-owned SDL event queue and use the production driver translators.
Backend mismatches and detached renderers are refused. Shutdown verifies renderer/window detachment and SDL/TTF release.
GPU application attachment is implemented by GpuApplicationExecution (below); Console uses NativeHosts. The standalone matrix retains its existing scope.

## GPU application execution

Windows target modes: `SDLGpuDriver` (D3D12 shader), `SDLGpuDriver-D3D12Fallback`
(real production CPU-composite/upload/blit fallback selected by the existing diagnostic simulated
pipeline-construction failure), and `SDLGpuDriver-Vulkan` (shader; focused F10 smoke qualified).
The four existing audited SDL Commander scenarios remain authoritative; arbitrary actions,
resize, held keys, text, drag/wheel and unreviewed scenarios refuse before launch.

The actual production GPU driver is initialized before TCommanderApp construction. Target-owned
SDL events pass through the production GPU driver translation and framework loop; no physical
input/focus automation. Actual backend, adapter/hardware, shader/fallback identity, window and
process, font/cell geometry, frame counters, hashes and capture boundary appear in checkpoint
JSON. Capture requests are served on the existing GPU render worker: the actual submitted
application snapshot is rendered a second time into a readable GPU texture of swapchain format,
then downloaded with a bounded fence. This is renderer-target evidence, not swapchain,
compositor or monitor readback. Normal shutdown joins the worker before GPU resource release.

D3D12 shader and fallback have focused menu/dialog/mouse coverage at 80x25 and representative
140x40 coverage. Runtime mismatches fail; another driver/path is never substituted. Native
captures remain Unreviewed, with semantic assertions separate from visual approval.
Workbench uses the existing Rendering & Drivers choices, eligibility/mark preservation,
custom grids, modeless progress/results and flat run.json/report.md outputs. Version 1 .twproj
files accept these additional mode values without adding required fields. Results Details
shows actual driver/backend/adapter/path/boundary alongside artifact and cleanup information.
