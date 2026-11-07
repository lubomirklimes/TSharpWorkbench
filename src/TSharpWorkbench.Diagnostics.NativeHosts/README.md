# TSharpWorkbench.Diagnostics.NativeHosts — native-host Layer C (Windows console hosts)

Qualifies what real Windows console hosts **display**: the pixels of a test-owned classic conhost or
Windows Terminal window running TSharpVision. Layer B (driver output translation) is driver-output qualification
(`TSharpWorkbench.Diagnostics.DriverOutput`); SDL pixels are native-pixel (`TSharpWorkbench.Diagnostics.NativePixels`).

```
TSharpWorkbench.Diagnostics.NativeHosts run [--output <dir>] [--commander <approved logical dir>] [--sessions id,...]
```

Artifacts go to `artifacts/native-hosts/run-*/` (flat): `<host>--<driver>--<scenario>--<grid>.native.png`,
the matching `.pixels.json`, `run.json` and `report.md`. Native screenshots are candidates only and never
baselines.

## How it works

- **Hosts.** Classic conhost is started directly (`conhost.exe <child>`, its own window, font pinned for that
  console only). Windows Terminal is started with `wt -w new --size C,R`. The captured window is the one the
  child reports through its own console (the console window, or the owner of the pseudo-console window), and
  for Windows Terminal it must be a window that did not exist before launch. Nothing else is touched.
- **Children.** In `app` mode a real `TApplication` runs on the Win32 Console driver through `AppLifecycleGuard`.
  In `screen` mode the driver-output fixture screens and approved Commander TVS1 screens are written through a driver's
  production output: Console (WriteConsoleOutputW), or Terminal (its VT stream attached to stdout, since the
  Terminal driver has no Windows TTY attach). A file control channel switches scenes and returns the host
  console buffer readback once it settles.
- **Capture.** `PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)` of that window into a DIB, from a
  per-monitor-DPI-aware process.
- **Checks** (`HostAnalysis`). A calibration screen locates the grid from two magenta corner cells and measures
  the host's rendered 16-colour palette. Every cell is then checked: background colour, glyph ink and its
  colour, full/half blocks, shade order, box lines reaching their edges and joining their neighbours, and Czech
  accents inked above their base letter. Redraws are diffed per cell against the buffer change, the caret is
  detected over a timed capture series, and a resize is checked for grid geometry. Negative controls (wrong
  background, shifted text, grid offset, blank capture, missing text) must be detected.
- **Input.** Classic conhost receives `WM_KEYDOWN`/`WM_KEYUP` posted to its own window (the host's keyboard path).
  Modifier chords and Windows Terminal use key records written into the child's own console input buffer by
  the `inject` helper. Nothing is injected globally.


## Real Commander Console execution

Run Configuration (F5) > Rendering & Drivers > Native driver > Windows Console uses the real
Win32ConsoleDriver and real Commander event loop in a private classic conhost. The existing authenticated
pipe, isolated CommanderSession, assertions, modeless Running Tests/Results and flat run.json/report.md
remain authoritative. `--driver Win32ConsoleDriver` is also accepted by the CLI and maintained Commander launcher.
Only F10 open/close, nested submenu, Window > Jobs status transition, and Copy/Rename here cancellation are eligible. Mouse, resize, held/text
input, arbitrary actions and GPU remain unavailable. Unsupported marked/group selections are refused before
launch; All eligible explicitly excludes unsupported scenarios. Native baselines are never approved here.

The Console adapter is the fixed internal NativeHosts provider, staged only by CommanderDiagnostics builds.
It launches conhost with a bounded lease helper, verifies the exact helper image and private console membership,
attaches the target, opens CONIN$/CONOUT$, and initializes the production driver before TCommanderApp creation.
Each supported key uses WriteConsoleInputW; acknowledgement requires production ReadKeyEvent consumption and
subsequent framework settling. This qualifies programmatic input, not a physical keyboard or keyboard layout.
Host PNGs use the existing NativeHosts PrintWindow client-area capture. Metadata records actual driver, target,
lease/launcher/window identities, DPI, font, palette, grid, screenshot dimensions and hashes; output readback is
separate driver-output evidence. Surrogates/shared consoles/mismatches/blank capture fail without fallback.
Normal exit uses native Alt+X plus the verified quit confirmation and Enter. Cancellation retains bounded
modal unwind and owned-target termination; the lease also watches target death. No system terminal settings change.
