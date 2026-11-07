# TSharpWorkbench

## Workspace and shell

Plain `tui` opens the **Start Page**: the product name, its purpose and the Workbench version (centred, with an empty
row before Recent projects; the window grows with the list instead of shortening it), up to eight recent
projects (newest first), Open project... and New project.... The status line names Enter (opens the highlighted project)
and Esc (leaves an empty desktop whose status line offers Ctrl+N / Ctrl+O). Help > About names the author, copyright and
license.
`tui --project <file>` opens a project and `tui --target <exe>` creates an untitled project, as before.

Menus: Project, Scenarios, Test, Results, Baselines, Window, Help. Keys: F1 Keyboard reference, F2 Refresh scenarios,
F3 Project Information, F4 Project Properties, F5 Run..., F6 Results, F10 menu, Alt+X exit, Ctrl+N/O/S project New/Open/Save, Alt+F3 close and Ctrl+F5 size/move the current
window. Fixed window numbers: Alt+1 Workspace, Alt+2 Scenarios, Alt+3 Run Configuration (the Run Monitor while a run
is active; Alt+3 never starts a run), Alt+4 Results, Alt+5 Comparison, Alt+6 Messages; Window lists them with the same
shortcuts. The menu bar letters are unique: Alt+P Project, Alt+S Scenarios, Alt+T Test, Alt+R Results, Alt+B Baselines,
Alt+W Window, Alt+H Help; no modeless window uses these letters as mnemonics.
F3 and F4 need an open project (otherwise the no-project message); inside a dialog they do nothing. The Workspace status
line lists F1 Help, F2 Refresh, F3 Info, F4 Properties, F5 Run, F6 Results, F10 Menu, Space Mark and Ins Mark next; beside a
notice the trailing keys are hidden first.

The Project menu is grouped: New / Open / Save / Save as / Close | Information... (F3), Properties... (F4) | Target
diagnostics..., Prepare a copy..., Fix original target... | Exit. Verify target lives in Target Diagnostics (and in Fix
original target, where it decides whether a fix beside another framework build is kept); Project Properties shows the
compact diagnostics with Fix issues... and Details....

The **Workspace** (window 1) shows the Groups pane, the Scenarios list (mark, scenario, ID, supported grids, last
result, note), a tool row with Search (Alt+E) above an empty row and the column headers, and the Details pane with the complete scenario definition, with one caption
row below. It has no buttons: Run... is F5 (Alt+U), Results F6 (Alt+L) and Properties... F4 (Alt+O), also in the menus.
Space or Enter marks, Ins marks and moves down; marks survive search and group filters. Target screen sizes are chosen
in Run Configuration. Test > Scenario details (Alt+D) moves focus to the Details pane.

**Project > Information...** is a read-only summary of the open project: display name, project file, target executable
and its diagnostic state, scenarios and attached files, marks, last run, run setup and runs folder. It updates while it
is open; F3 opens it as well. It has only Close (Properties is F4, Target diagnostics is in the Project menu). When the target is not ready, the message line says so once,
with the exact first problem (for example Framework incompatible) and these routes.

**Project Properties** (F4 or Alt+O) edits the project name, executable, runs folder, baseline store and the allowed
capabilities (Network, Child processes, Devices; elevation is never allowed). Arguments, environment and scenario
files are shown read-only (scenario files are managed in the Scenarios menu). Changing the executable states before OK
that the scenarios reload and marks are cleared.

## Scenarios menu and window

External JSON scenario documents are attached to the project without editing the `.twproj`: New project → executable →
Save → **Scenarios > Add files... / Add folder...** → mark scenarios → Run (F5) → Results.

- **Add files...** opens the file dialog on `*.json`; each **Add** validates and attaches one document and the dialog
  opens again in the same folder (the framework dialog selects one file at a time); **Done** ends. Missing files,
  malformed JSON, other JSON (manifests, `run.json`), unsupported schema versions, invalid documents, duplicate paths
  or IDs and a second scenario kind are refused with the file and the cause; the project stays unchanged.
- **Add folder...** shows what a folder contains before anything changes: valid documents to attach, already attached,
  rejected (with the cause) and ignored JSON. Subfolders are not included unless *Include subfolders* is ticked.
- **Manage scenarios...** (Alt+2) opens the modeless **Scenarios** window: status, scenario ID, type and file of every
  attached document, details and the problem of the selected one, and New, Add files, Add folder, Remove, Open in
  editor, Validate, Refresh. Ins adds files, Del removes, F2 refreshes, Esc closes.
- The menu reads New scenario... / Edit scenario... / Remove scenario... | Add files... / Add folder... | Manage
  scenarios... (Alt+2) / Validate scenarios | Refresh catalog (F2). Edit and Remove act on the selected attached file
  (Scenarios window, or an external scenario focused in the Workspace) and say what to select otherwise.
- **Remove scenario...** detaches the reference only; the JSON file is never deleted (the confirmation says so). Marks of removed scenarios are
  dropped with a notice, never remapped.
- **Edit scenario...** opens the internal structured editor; its **External editor** button starts `TSHARPWORKBENCH_EDITOR` (an editor executable) when set, otherwise Notepad (Windows),
  `open -t` (macOS) or `xdg-open`; the file is passed as one argument without a shell. Refresh (F2) reloads it.
- **New scenario...** writes a minimal valid schema-v1 document: *Proxy logical* (one checkpoint of the application's
  first screen) or *fixture-bound* (a fixture the current target advertises, known after a refresh, with its first
  advertised checkpoint expectation). It is validated before it is written, then attached.
- **Validate scenarios** checks existence, JSON, schema/version, document rules, duplicate IDs, the project's single
  scenario kind and, for fixture-bound documents, the binding against the target's fixtures (discovered in the
  background when needed; no scenario runs). **Refresh catalog** (F2) reloads every document and rebinds.

Loading is per file: a missing or invalid document is left out of the catalog and reported (Scenarios window, Messages)
instead of hiding the other scenarios. A Proxy project never launches the target for discovery; an ordinary application
without the TSharpWorkbench diagnostic host and without attached documents is not launched either, and the Workspace
says *No external scenarios are attached. Use Scenarios > Add Files or Add Folder.* The CLI keeps strict loading.

Every message goes to one notification channel: the status line shows the latest notice in every window, the
**Messages** window (Alt+6) keeps the session history with technical details (Copy details, Clear), and blocking
failures open a standard error dialog. Search text, group focus and collapsed groups are user view state stored in
user settings; only properties, marked scenarios and saved run defaults mark a project as changed. The **Unsaved
Project Changes** dialog lists the changed groups and has Cancel as its default: Enter, Esc and the close icon cancel,
Save and Discard need an explicit choice. Open project and Save as start in the current project's folder.

## Project workspace

The primary workspace is a modeless `TWindow` in the desktop. Closing a project keeps the application open;
Esc in the workspace does nothing; it never cancels a run. Use Project > Exit / Alt+X to quit. Window
frame controls provide move/resize/zoom/restore; Window > Workspace and Next/Previous Window restore focus.

Project > New project, Open project, Save, Save as and Close project manage one `.twproj` JSON project, with the
Unsaved Project Changes confirmation. Configuration save never writes to the tested application's source or
baseline store. See [PROJECT-FORMAT.md](PROJECT-FORMAT.md) and [SampleTarget.twproj](SampleTarget.twproj).

The left group tree is built from current catalog prefixes. Arrows and Enter expand/collapse and navigate; a mouse
click focuses a group. Counts include its subtree. The upper right test list filters by that group and Search (ID or
name). Space/Enter or a double-click toggles marks; marks survive group/search changes. Mark Group / Mark All omit
expected-failure examples; explicit marking still permits them. The lower right details pane is always present,
wraps long text and scrolls with Up/Down/PgUp/PgDn when focused with Tab. It shows identity, description, group,
grid support, expected outcome/assertions, baseline status, session result and capability limits.

## Run Configuration and Run Monitor

Test > Run / F5 opens the single modal **Run Configuration** dialog (no nested settings dialogs). Its `TGroupBox`
sections are What to run (marked / current group / all runnable, with counts), Test mode (Functional, Visual
regression, Native driver), Target screen sizes (80x25, 140x40 and a comma-separated custom list), Target display
driver (Headless, SDL window, SDL GPU window, Windows console; the native diagnostic backends in Native driver mode),
Native evidence (choices per backend from `ProxyCaptureKinds.ForBackend`), Visual baselines (store state; the store is
edited in Project Properties) and Limits and inspection. A live Summary lists executions, driver, evidence, baseline
behaviour, output folder and every problem (`!`); Run is disabled until no problem remains. Run has the default focus,
so F5 Enter repeats the session configuration. Esc, Cancel and the close icon change nothing. Unavailable choices stay
visible with a reason; nothing falls back silently. The target driver never changes the Workbench's own window.

Running changes only the in-memory **session run configuration** (initialised from the project's run defaults when the
project opens); the project is not marked changed. Tick **Save these settings as project defaults** (unchecked on every
open) to copy them into the project defaults; the project then becomes changed only if they differ, and Ctrl+S writes
it. The workspace header shows the session setup, prefixed `(session only)` while it differs from the defaults. The
command-line runner always uses the saved project defaults. Each run uses an immutable snapshot of its request.

The modeless **Run Monitor** (window 3, Alt+3 while a run is active / Window > Run configuration / monitor, bottom right) opens without taking focus. It shows
the state (Starting, Running, CANCELLING, Finished, CANCELLED (n of m completed), ENDED UNEXPECTEDLY), a `TProgressBar`
with "n of m", Passed / Failed / Blocked / Cancelled (aborted executions never count as failed), visual verdicts, target
driver and sizes, the latest event and the run folder. Results is its default button; Cancel run is never the default.
Esc returns to the previous window without cancelling; Close while a run is active keeps the monitor and the run.
Cancel the run only with Run Monitor > Cancel run or Test > Cancel run. New, Open, Close project and Exit during a run
show **Tests Are Running** (Keep running is the default; Esc keeps running); choosing cancellation waits for the owned
target cleanup before continuing, then the Unsaved Project Changes check applies. A runner failure (not a test failure)
shows **Run Ended Unexpectedly** with cause, kept executions, cleanup outcome and next step; details are in Messages.
Opening a project only discovers metadata. Saved IDs absent from the current catalog are reported and removed;
the changed selection stays dirty. A rebuilt target must be refreshed before a run can start.

Logical NullDriver and approved TVS1 visual comparison remain available. Native SDL software execution still covers
the four audited Commander scenarios documented below. Console supports the four keyboard workflows listed in below; GPU modes are documented in below. Run Configuration,
per-project preferences, modeless progress/results, baseline menus and comparison are described below. Inline grids
remain available as initial defaults for Run Configuration. Repeat Last Run is not offered.

Development tooling only. Discovers scenarios from a compiled TSharpVision test target, runs them in isolated target
processes and writes logical captures, semantic evidence, assertion results and a report. CLI and TUI share
`ScenarioRunner` from `../TSharpWorkbench.Testing` (see its README for the host, protocol, evidence and output contract).

## Build and capture from current source

The Workbench-owned sample fixture (`../../tests/TSharpWorkbench.SampleTarget`) needs nothing else: no earlier binaries,
gallery manifests or local package feeds. Prerequisites: .NET 10 SDK and PowerShell 7 (`pwsh`). One command builds both
projects (incrementally) and captures into a new flat run directory under `artifacts/testworkbench/`:

```powershell
./scripts/capture-sample-target.ps1                              # every scenario expected to pass, its default grids
./scripts/capture-sample-target.ps1 -Group sample/menu -Size 80x25,100x30
./scripts/capture-sample-target.ps1 -Scenario selftest/failure/wrong-expectations -IncludeExpectedFailures   # exits 1
./scripts/capture-sample-target.ps1 -Output C:/somewhere/else -Inspect all -Results C:/somewhere/results.json -NoBuild
```

The script performs no Git operation and exits with the CLI's code.

## CLI

```
dotnet <TSharpWorkbench.dll> list      --target <exe> [--verbose]
dotnet <TSharpWorkbench.dll> groups    --target <exe>
dotnet <TSharpWorkbench.dll> describe  --target <exe> --scenario <id,id>
dotnet <TSharpWorkbench.dll> run       --target <exe> (--scenario <id,id> | --group <group> | --all) --output <dir>
          [--size WxH[,WxH...]|standard] [--inspect declared|all|none] [--timeout <ms>] [--scenario-timeout <s>]
          [--allow network,processes,devices] [--include-expected-failures] [--results <file>] [--verbose]
          [--baselines <visual-baselines.json>]
dotnet <TSharpWorkbench.dll> baseline status  --baselines <manifest>
dotnet <TSharpWorkbench.dll> baseline approve --baselines <manifest> --run <run-dir> --candidate <key[,key...]>
          --confirm approve-<count> [--replace]
dotnet <TSharpWorkbench.dll> help
```

`--target` is the test target executable (`.exe` on Windows, the apphost without extension elsewhere). Everything is read
from the target's authenticated handshake. `--size` defaults to each scenario's default grids (normally 80x25 and
140x40; `standard` selects exactly those); grids are 20x6..200x100 and must lie in the scenario's range, otherwise that
execution is `Blocked`. `--all` and `--group` skip scenarios declared as expected failures unless
`--include-expected-failures`. `--verbose` prints every non-passing assertion with expected, actual and location.

Each execution runs once per grid in a fresh process with fresh empty working and state directories. Outcomes: `Passed`,
`Failed` (assertion or inspection failure), `Error`, `TimedOut`, `Blocked`, `Aborted`, `Skipped`. `Skipped`
means a verified platform/driver prerequisite is not met (a native driver that cannot run the scenario, a Windows-only
driver elsewhere); its reason starts with "Skipped - platform/driver unsupported:". It is counted, never a failure and
never a pass. Exit codes: 0 all passed (skipped executions allowed when at least one passed), 1 any other outcome, an
all-skipped run or a run failure, 2 invalid arguments. Ctrl+C requests bounded cleanup.

A run never reads earlier runs; output directories are disposable. Automated success leaves visual review `Unreviewed`.
`--baselines` compares the checkpoints a manifest gates with its approved baselines and adds diff and unreviewed
candidate files (see the Testing README, "Visual baselines"); exit code 3 = scenarios passed, visual gate did not. A
missing baseline is never a pass. `baseline approve` is the only command that writes approved baselines. Captures are NullDriver logical rendering (Layer A), never native
screenshots.

## TUI

Build the Workbench and the sample target, then launch the interactive developer workflow:

```powershell
dotnet build src/TSharpWorkbench -m:1
dotnet build tests/TSharpWorkbench.SampleTarget -m:1
dotnet src/TSharpWorkbench/bin/Debug/net10.0/TSharpWorkbench.dll tui --target tests/TSharpWorkbench.SampleTarget/bin/Debug/net10.0/TSharpWorkbench.SampleTarget.exe
```

On Linux/macOS use the target apphost without `.exe`. **Target** means a diagnostic application executable that implements
the authenticated TestTargetHost protocol. A normal product executable may refuse discovery; rebuild its diagnostic
configuration and use that executable. A prepared script can pass `--target` and `--output` so browsing is unnecessary.

The **Run Tests** screen shows the target name and loaded scenario count. A provided or remembered target loads
automatically; F2 refreshes its catalog. Project Properties > Browse... selects a target. Changing target discards the
previous catalog and marks before discovery; refreshing the same unchanged build keeps marks whose stable IDs still exist.
The Workbench checks the selected apphost and its companion managed assembly at discovery and run boundaries. A rebuilt
target at the same path invalidates its catalog and marks; refresh with F2 and select from the current build before running.

1. Choose **All scenarios** or a group to filter the list. Search matches scenario name or ID, ignoring case.
2. Tab to scenarios; arrows move, Space or Enter marks, and double-click toggles a mark. Marks survive filtering.
   The marked count includes hidden marks. **Test > Clear marks** removes all marks. The menu can mark the whole group
   (excluding deliberate expected failures).
3. **Run... / F5** opens Run Configuration (see above). Explicitly marked expected failures remain runnable. Enter in
   the workspace's group or text field never launches a run. The summary explains eligibility and incompatible grids.
4. Run freezes the request and starts the batch; Cancel discards the edit copy. The session configuration is kept
   for the next F5; only the Save-as-defaults option changes the project.
5. Watch the Run Monitor (Alt+3). **Cancel run** waits for the runner's owned-process cleanup; Esc never cancels.
   Alt+X during a run asks first (Tests Are Running).
6. **Results / F6** (window 4) shows the run header (run, setup, output, verdict) and the executions with separate
   Result and Visual columns. Enter inspects an execution in place: its checkpoints (including "not reached" ones) and
   the Details pane with assertions (expected/actual as recorded), inspection errors, visual status and evidence rows.
   Enter on an evidence row opens the file; < Run / Run > (Ctrl+Left/Right) move through the run folders; Re-run...
   opens Run Configuration for the selected execution. Esc closes Results; the project stays available in its own window.

**Project Properties** holds the full target path and editable **runs folder** (output root) with Browse... buttons
(Alt+B executable, Alt+S runs folder, Alt+O baseline store; Alt+W toggles Network). A line above OK/Cancel shows why OK
was refused and warns when a changed executable will clear the marked scenarios.
The standalone default root is `%LOCALAPPDATA%/TSharpVision/TestWorkbench/runs` (the platform's local application-data
folder on other systems). Existing saved roots stay in use. Every actual run creates a unique flat `run-<UTC>-<id>`
folder under that root; historical runs are preserved. The Results Overview lists the actual run folder, `run.json`
and `report.md`; Run folder opens the folder in your file manager and evidence rows open PNGs in your normal image
viewer (the TUI does not embed an image viewer). Checkpoint evidence rows also locate TVS1, checkpoint JSON and
available visual diff/candidate files, each labelled with its capture layer.

Target, output root, group filter, search, marked stable IDs and size persist in
`%LOCALAPPDATA%/TSharpVision/TestWorkbench/settings.json`. Explicit launch arguments override saved preferences;
marks/search/group are reused only for the same target. TUI `--scenario`, `--group`, `--size`, `--output`, inspection,
capability grants, timeouts and `--baselines` use the existing runner. Launch options and IPC credentials are not saved.
For CI use the CLI examples above: its selection semantics, required output argument and exit codes are unchanged.

Errors preserve the runner's specific reason (timeout, crash, refused capability, assertion, baseline problem).
**Window > Messages (Alt+6)** retains full discovery/configuration exception evidence. For a stale diagnostic build,
rebuild the diagnostic target using matching Testing binaries, select it and refresh with F2.

### Test mode and display driver

The default application-scenario launch path is **Logical UI â€” NullDriver**. It tests cell state, geometry, simulated
input and assertions, captures TVS1 and logical PNG, and can compare visual baselines. Logical PNG renders cells;
it is not a native screenshot. **Mode** on the main form (or **Menu â†’ Select test mode**) selects the target mode;
**Menu â†’ Test mode capabilities** explains availability.

On Windows, **Native SDLDriver / software** executes these audited real Commander workflows through production SDL
rendering and target-owned `SDL_PushEvent` input:

- `commander/menu/f10-open-close`
- `commander/menu-status/nested-submenu`
- `commander/input/menu-mouse`
- `commander/keyboard/copy-rename-here`

Build the Commander diagnostic target with its maintained `scripts/capture-commander-diagnostics.ps1`, which stages the
fixed NativePixels provider only in diagnostic builds. For example, from Commander:

```powershell
./scripts/capture-commander-diagnostics.ps1 -Driver SDLDriver -Scenario commander/input/menu-mouse -Size 80x25
./scripts/capture-commander-diagnostics.ps1 -Command tui -Driver SDLDriver
```

The corresponding Workbench CLI option is `--driver SDLDriver`; `--driver NullDriver` remains the default. Native PNGs
are read from the actual Commander's SDL_Renderer target before present; they do not prove compositor presentation.
TVS1 cells remain semantic assertion evidence. Checkpoint JSON identifies executable, assembly/MVID, production driver,
backend, process/HWND, font, cells, native dimensions and input routing. Results keeps the same hierarchy and artifact
locations. Input is programmatic native driver injection, not physical keyboard/mouse qualification.

Other scenario IDs and unsupported steps are blocked before execution. Resize and viewer application integration remain unavailable. Console and GPU modes are documented below. Native mode refuses logical visual baseline gates, an unstaged provider, a different
runtime backend and a logical handshake; it never silently falls back or approves native baselines.

The Workbench display driver, target execution driver and capture layer are independent. Driver Output qualifies
driver translation through `src/TSharpWorkbench.Diagnostics.DriverOutput`; Native Pixels and Native Hosts
 qualify real renderers/hosts through their separate diagnostic programs. These tools do not provide an arbitrary
application-scenario launch path automatically. Only the explicitly audited SDL/Console/GPU adapters documented here are supported.
An unsupported `tui --driver` is refused, with no fallback.

The TUI references the Win32 console driver and the ANSI terminal driver, so it is visible on Windows consoles and on
Linux/macOS terminals. Product display policy: Windows Console/SDL; Linux/macOS Terminal/SDL. This executable currently
references Console and Terminal; it does not add an SDL display selector or attach the ANSI driver interactively on Windows.

## Applications other than the sample

An application becomes a target by calling `TestTargetHost.Run` from its entry point when
`TestTargetHost.IsActivated(args)` (see the Testing README). TSharpCommander already has a prepared diagnostic build/launch
workflow that supplies target and output paths. uses the sample target for validation; real Commander workflow
qualification and any Commander-side changes belong to the Commander qualification.

## Run and results workflow

F5 and Test > Run open the same compact modal **Run Configuration** dialog. Choose Functional / Interaction,
Visual Regression, or Rendering & Drivers; choose marked tests, current group, or all eligible tests. Both standard grids
and a validated custom grid can be selected together. The scrollable summary identifies target, driver, eligibility,
incompatible grids, visual gating, baseline store and actual output root. Options edits the manifest, comparison/candidate
policy, inspection and bounded timeouts. Cancel commits none of the edit copy; Run updates the project dirty state.
Save the `.twproj` separately to persist it. See [PROJECT-FORMAT.md](PROJECT-FORMAT.md).

Functional scenarios use NullDriver and semantic assertions without requiring baselines. Visual mode runs those same
scenarios/assertions and the existing TVS1 gate. Candidate mode retains comparisons and emits unreviewed canonical review
copies, including matching captures. Missing, invalid, mismatch, normalization error, not-captured, not-run and no relevant
gated capture remain distinct. Assertion outcomes and visual review are separate; captures never become approved by running.

Rendering & Drivers identifies Layer B driver-output qualification and Layer C native pixels separately. The executable
application paths are Windows SDL software and owned Console for their audited Commander subsets. Test > Show supported native tests / all
filters the explorer without removing hidden marks. Marked/group requests containing unsupported native scenarios are refused;
all-eligible native scope states its exclusion of unsupported scenarios. Standalone fixture paths have no
universal application-driver picker. The native runner refuses logical gating and never falls back.

**Run Monitor**, **Results**, and **Baseline Comparison** are modeless TWindows (the section below describes the current Results). Switch using Window, resize/zoom
with their frames, and continue browsing the project during execution. Running Tests shows operation, grid, counts, paths and
bounded cancellation/cleanup; closing it during work requests cancellation and refuses closure until cleanup finishes.
Results shows live counts while a batch runs; complete execution/checkpoint rows arrive after the runner finishes cleanup.
F6 selects Results. Esc closes Results without quitting the application. F10 opens the application menu; Alt+X retains safe exit.
Only one batch is active; project replacement/closure is guarded until its cleanup completes.

### Results, captures and baselines

Results (window 4, F6 / Alt+4 / Results > Show results) is a master/detail window: a four-line header (run identity,
date, duration, position in the runs folder, session or historical run; setup; output folder; verdict with completed,
passed, failed, blocked and cancelled counts and the visual counts), the Executions list (marker, Result, Visual, Grid,
Scenario, Time) with an All / Problems only filter, the Checkpoints list of the selected execution and the Details
inspector. Execution and visual verdicts are separate: Aborted executions are CANCELLED, never FAILED; expected failures
show "Failed (expected)". Time is measured by the Workbench for runs of this session; run.json records no per-execution
duration, so historical runs show "-". Evidence rows keep their provenance: logical PNG "cells rendered by Workbench",
TVS1 cells, metadata, comparison and candidate files, and native artifacts with their boundary (SDL renderer pixels,
SDL GPU completed frame, conhost console cells, native-driver PNG). A file deleted after loading is shown "! missing"
and opening it only warns. Actions: Open image, Native, Compare, Review candidate..., Copy path; < Run / Run >,
Open run..., Re-run..., Run folder, Close. Disabled actions are explained under "Unavailable here" in Details.

Run navigation lists the `run-*` folders next to the shown run (runner folder names carry the UTC start time; within
one second the folder creation time decides). Unreadable folders are skipped and reported; an invalid run.json never
replaces the shown run, and navigation never writes. Results > Open run... still opens any run.json.

Re-run... (button Alt+E, Results > Re-run selected...) opens Run Configuration with a fourth scope, "Re-run from
Results": the selected scenario at its grid with the setup that run recorded (the session request, or run.json and
workbench-request.json of a historical run; fields neither file records are named in the Summary). Nothing runs until
Run is pressed; the run gets a new run folder, the original run is not touched, and a Re-run changes neither the
session configuration nor the project defaults (Save as defaults is disabled for it). A scenario missing from the
current catalog, or a driver this catalog cannot run, is a visible problem; nothing is substituted.

Baseline Comparison (window 5, Alt+5, Results > Open comparison, Baselines > Compare with baseline) shows the
checkpoint, baseline key, store, approved cells hash, run, status and capture provenance, the cell viewport with Diff /
Expected / Actual (Alt+D/E/A), the focused cell, the list of differences (Enter jumps), Next / Previous (N / P) and
Review candidate.... A missing baseline shows the candidate only. The existing guards stay: a changed approved baseline
or an unavailable original store refuses with a message. Native evidence (there are no approved native baselines)
opens the Native Evidence Comparison: the native and logical artifacts with Open file / Native / Open logical PNG.

Review candidate... opens one modal dialog for the selected checkpoint: "Approve Baseline Candidate" when the store has
no approved baseline for it, "Replace Approved Baseline" when it has one. It shows the run, scenario, grid, checkpoint,
key, store, current state, candidate identity and integrity, capture layer, normalization and exactly which files
change. Approve/Replace stays disabled until "I inspected this candidate..." is ticked; Cancel is the default, and Esc,
the close icon or Enter on another control write nothing. A final confirmation names the target (Cancel default).
The candidate and store hashes bound when the dialog opened are re-checked, and all writes still go through
`VisualBaselineSet.Approve` (one explicit key, `approve-1`, replace only when the store has a baseline). Failures show a
readable error with details in Messages and leave the store unchanged. Baselines > Baseline store status lists the
store state and every gated checkpoint; an unreadable store is shown as such. Native approval remains unavailable.

Baselines are environment-specific: the environment is the operating system, the driver the logical
cells came from (NullDriver for diagnostic runs, the Proxy backend for ordinary applications) and the capture type; the
grid is part of the key. The flat `approved/<key>.*` slot keeps its format and holds the one environment its metadata
records (files written before this stage: their platform, NullDriver, logical); every other environment is approved into
`approved/<platform>-<driver>-<capture>/<key>.*`. A run compares only with its own environment. Without one the checkpoint
is MISSING ("no matching baseline"), and the reason names the environments that have a baseline and the dimension that
differs; another OS's or driver's baseline is never compared, copied or used as a fallback, and approving never replaces
it. `baseline status --driver <driver>` lists one environment.

Results, Comparison and the Run Monitor remember their position and size in user settings.

Results > Open Run loads completed `run.json` with its `report.md` and validates flat artifact references and TVS1 hashes. New
Workbench runs also write `workbench-request.json` containing the effective configuration. Older visual metadata recorded only
a manifest filename: statuses/artifacts load, but comparison/approval requires recoverable original manifest provenance. No
report database or production/public Testing API was added. CLI parsing, runner semantics, output formats and exit codes are unchanged.


## Real Commander Console execution

Run Configuration (F5) > Rendering & Drivers > Select... > Windows Console uses the real
Win32ConsoleDriver and real Commander event loop in a private classic conhost. The existing authenticated
pipe, isolated CommanderSession, assertions, modeless Running Tests/Results and flat run.json/report.md
remain authoritative. `--driver Win32ConsoleDriver` is also accepted by the CLI and maintained Commander launcher.
Only F10 open/close, nested submenu, Window > Jobs status transition, and Copy/Rename here cancellation are eligible. Mouse, resize, held/text
input, arbitrary actions remain unavailable; GPU modes are documented below. Unsupported marked/group selections are refused before
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


## Real Commander Console execution

Run Configuration (F5) > Rendering & Drivers > Select... > Windows Console uses the real
Win32ConsoleDriver and real Commander event loop in a private classic conhost. The existing authenticated
pipe, isolated CommanderSession, assertions, modeless Running Tests/Results and flat run.json/report.md
remain authoritative. `--driver Win32ConsoleDriver` is also accepted by the CLI and maintained Commander launcher.
Only F10 open/close, nested submenu, Window > Jobs status transition, and Copy/Rename here cancellation are eligible. Mouse, resize, held/text
input, arbitrary actions remain unavailable; GPU modes are documented below. Unsupported marked/group selections are refused before
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


## Run Configuration and CI handoff

Run Configuration is a centered 76x23 modal at both 80x25 and 140x40 hosts. Type/scope sit above a compact
Driver row. Functional and Visual use NullDriver. Rendering enables Select, which opens a keyboard/mouse
backend picker populated from the loaded target/platform capabilities, independently of current marks.
Saved unsupported choices remain explicit and cannot run. Standalone driver/renderer fixtures are separate tools.

Standard and custom grids can be checked together. Width/Height have separate labels and input cells; inactive
fields are visibly disabled and retain valid dimensions. No size substitution occurs. The five-row summary shows
short target identity, counts and requested executions; blocking warnings come first. Tab to it and use arrows,
PgUp/PgDn, the mouse wheel or scrollbar to inspect more rows. Options exposes exact read-only target/output paths
with horizontal navigation and copying, plus the existing baseline manifest, policy and timeouts. Applying Options
then cancelling Run discards everything. Project Save remains a separate action after accepting a run.

Using prepared Commander diagnostics (no build or consumer-source change):

```powershell
$wb = '<workbench-build-output>/TSharpWorkbench.dll'
$target = '<commander-build-output>/TSharpCommander.exe'
$baselines = '<commander-source-root>/diagnostics/CommanderTestTarget/VisualBaselines/visual-baselines.json'
dotnet $wb tui --target $target --output artifacts/commander-runs
dotnet $wb list --target $target
dotnet $wb groups --target $target
dotnet $wb run --target $target --scenario commander/menu/f10-open-close --size 80x25 --driver NullDriver --output artifacts/ci-logical --results artifacts/ci-logical.json
dotnet $wb run --target $target --scenario commander/input/dialog-mouse --size 80x25 --driver NullDriver --baselines $baselines --output artifacts/ci-visual
```

Catalog/list/groups and NullDriver executions are non-interactive and need no desktop input. Native Console/SDL/GPU
require Windows desktop/window capture access; GPU additionally requires the supported hardware/API/path.
TUI host display requires an interactive terminal. Logical PNG/TVS1 captures are Layer A; SDL/GPU PNGs are
renderer-target evidence; Console PNGs are private host-window evidence. Never treat semantic Passed as native
pixel approval. Native captures remain Unreviewed. Inspect run.json driver/checkpoint metadata and nativeLifecycle
rather than inferring provenance from the extension. Failed runs retain flat run.json/report.md, checkpoint JSON,
TVS1/PNG and visual diff/candidate artifacts where capture succeeded. Exit codes: 0 success, 1 scenario/run failure,
2 invalid arguments, 3 semantic success with a failed logical visual gate.

Existing repository CI builds/tests the solution on Windows/Linux/macOS but has no sibling Commander checkout,
prepared diagnostic target or approved consumer store. A future main-roadmap job should supply those explicitly,
run the logical command above, preserve its exit code and upload results even on failure. Keep native Windows
qualification on a desktop/hardware-capable worker. No pipeline, packaging, publication, or work is added here.

Project responsibilities and target execution: [architecture](../../docs/architecture.md) and [repository structure](../../docs/structure.md).


## External JSON scenarios

Version-1 `.twproj` optionally contains `scenarioFiles`: explicit project-relative JSON paths. Compiled fixtures and external identities coexist; CLI `run project.twproj --scenario <id>` and the existing TUI use the same loader and runner. See [declarative scenarios](../../docs/scenarios.md) for the schema, action/assertion contract, retry behavior, baseline references and project exit codes.
