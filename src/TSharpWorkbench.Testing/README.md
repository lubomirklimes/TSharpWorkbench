# TSharpWorkbench.Testing

Development-only tooling: logical screen capture, semantic inspection, assertions, the local diagnostic protocol, the
framework-neutral target host and the controller (`ScenarioRunner`) used by TestWorkbench's CLI and TUI. Not packable by
default and not in the release allowlist (`-p:PackDiagnosticTooling=true` opts in for local tooling packs). It references
only the core framework: no application, xUnit, SDL, native font or interactive driver dependency.

adds an internal, diagnostics-only native execution adapter for the audited Commander SDL subset described in
the Workbench README. Testing keeps its Core-only compile-time dependency. The fixed NativePixels provider is staged by
the diagnostic application and loaded by assembly identity; no arbitrary provider name/path crosses the pipe. It selects
and initializes the production driver before application construction. Discovery stays driver-free and logical execution
stays the default. `TestTargetContext.Driver` remains the logical NullDriver API and throws in native mode; native-unsupported
scenario actions/steps are refused rather than mapped to simulated input.

The existing v1 checkpoint contract has an optional additive `Native` string dictionary, versioned
`tsharpvision.testing/native.1`. It transports native PNG bytes and capture provenance under the existing framing limit.
The controller verifies driver identity and refuses missing native evidence. TVS1 remains the logical semantic reference;
native PNGs and pixel hashes come from actual renderer readback. No Core/released API or public native scenario framework
is introduced. The adapter's registration, lifecycle and launch-mode property are internal friend contracts.

## Pieces

| File | Side | Role |
|---|---|---|
| `TestTargetHost.cs` | target | `TestTargetHost`, `TestTargetDefinition`, `TestScenario`, `TestTargetContext`, `TestKeys`, input tracker |
| `Inspection.cs` | target (+ in-process) | `ViewTreeInspector`, `MenuInspector`, `StatusInspector`, `Inspectors`, capture records |
| `Assertions.cs` | controller (+ in-process) | `AssertionSpec`, `UiAssert`, `ViewSelector`, `AssertionEvaluator`, `Geometry` |
| `ScenarioRunner.cs` | controller | discovery, execution, `RunOptions`, flat run writer, `TargetSession`, orphan sweep |
| `RunReport.cs` | controller | `report.md` |
| `ScenarioCatalog.cs` | both | `ScenarioDescriptor`, `ScenarioGrid`, `ScenarioSafety`, `ScenarioStep`, selection, `ArtifactNames`, results |
| `Protocol.cs` | both | Protocol v1 records, schema identity, authentication, bounded framing |
| `ScreenSnapshot.cs`, `LogicalPng.cs` | both | TVS1 cells and the logical PNG renderer |
| `LocalPipe.cs` | target | current-user named pipe |
| `VisualComparison.cs` | controller | `ScreenComparer`: exact cell comparison, structured differences |
| `VisualNormalization.cs` | controller | `VisualNormalizationRule`, `VisualNormalizer`: explicit, bounded dynamic-cell masking |
| `VisualBaselines.cs` | controller | manifest, approved store, `Approve`, per-checkpoint gate, `VisualDiffImage` |
| `VisualReport.cs` | controller | `visual.json` and the report.md visual section |

## Making an application a test target

The application registers its scenarios and calls the host from its entry point. No knowledge of the pipe protocol is
needed:

```csharp
if (TestTargetHost.IsActivated(args))
    return TestTargetHost.Run(new TestTargetDefinition
    {
        Scenarios = [
            new TestScenario("app/menu/file", "File menu", "app/menu", "Opens File with Alt+F.")
                .Kind(ScenarioKinds.Menu).Grid(60, 20)               // optional: kind, grid range, metadata, safety
                .Checkpoint("initial", c => c.NoModalView)
                .Key("kbAltF")
                .Checkpoint("menu-open", null, "File drop-down", [Protocol.EvidenceKinds.Menu],
                    UiAssert.MenuDepth("one-level", 1), UiAssert.MenuSelected("first-entry", "New"))
                .Key("kbEsc")
                .Checkpoint("menu-closed", c => c.NoModalView, null, null, UiAssert.MenuDepth("closed", 0)),
        ],
        CreateApplication = context => new MyApp(),   // NullDriver (behind the input tracker) is already installed
    });
```

Only registered names run; nothing executable crosses the wire. Steps:

- `Key(name)` — a `Keys` constant name, **logical input** read by the event loop (see Input acknowledgement, detail, inputState).
- `Action(name, delegate)` — a **direct** application action (setup, background producers). Recorded as `action`, never as
  user input.
- `Resize(w, h)` — a real logical resize: `NullDriver.SimulateResize` → `cmScreenResized` → `TProgram.HandleScreenResize`.
  The host verifies that both the screen and the application bounds changed; later checkpoints use the new grid.
- `Checkpoint(name, expect?, note?, evidence?, params assertions)` — waits (bounded) until the optional expectation holds
  and the composed screen is identical over three consecutive event-loop passes, then captures the **whole**
  `TScreen.ScreenBuffer` plus the requested semantic evidence in the same UI pass. Evidence needed by the assertions is
  requested automatically.

### Simulated keyboard and mouse

Stateful logical devices (`InputSimulation.cs`); each step becomes ordinary `TEvent`s handed to the loop where a driver
would (key path for keys and modifiers, `TEventQueue` via `PumpMessages` for mouse). A step is validated completely
before any event exists; then each event is submitted alone and settles before the next.

| DSL | Step (`kind name`) | Events |
|---|---|---|
| `ModifierDown(TestModifiers.Shift \| Ctrl)` / `ModifierUp(...)` | `modifier shift-down` … `alt-up` | `evModifierChanged` with the new logical state; refused when already held / not held |
| `WithModifiers(m, body)` | downs, body, ups | sugar |
| `Key(name)` | `key kbF5` | nothing held: the historical key-down; modifiers held: composed like the key contract (`kbShiftF5` + Shift state; Alt > Ctrl > Shift) |
| `KeyDown(name)` / `KeyUp(name)` | `key-down` / `key-up` | `evKeyDown` held until `evKeyUp` with the press's identity |
| `Text("abc")` | `text abc` | one key-down per character; Ctrl/Alt + letters/digits become their constants |
| `MouseMove(p)` | `mouse move@x,y` | `evMouseMove` carrying held buttons (a drag) and modifiers |
| `MouseDown(p, b)` / `MouseUp(p, b)` | `mouse down-left@x,y` / `up-left@…` | move if needed, press / release (empty button mask) |
| `Click(p)`, `RightClick(p)`, `DoubleClick(p)` | `click-left`, `click-right`, `double-left` | move?, down, up (double: second down carries `meDoubleClick`) |
| `Drag(from, to, via)` | down, moves, up | one step per event |
| `Wheel(p, ±n)`, `HorizontalWheel(p, ±n)` | `mouse wheel-down@x,y*3` | n wheel events (+ = up / right) |
| `ReleaseAll()` | `release all` | buttons, keys, then modifiers |

Points: `(x, y)` literal cells (negative = from the right/bottom edge, `-1` = last column), or `TestPoint.Named("id")`
registered with `Point("id", ctx => TPoint?)` and resolved on the UI thread. Nothing is clamped: a cell outside the
current grid, an unavailable point, a button chord, releasing what is not held — all refuse the step with the held state
(`[last input: …; input state: modifiers=…; keys=…; buttons=…; pointer=…]`). Every interaction record carries
`inputState`. Completion releases anything still held through the pipeline and reports it in the result reason; an
aborted execution drops undelivered input without synthesizing releases. Each execution is a fresh process, so state
never leaks between scenarios. The host closes the framework's wall-clock double-click window
(`TEventQueue.DoubleDelay`) in the target process; `evMouseAuto` (time-based auto-repeat) is not simulated. The tracker
reports `SupportsMouse`, `KeyReleaseEvents` and `StandaloneModifierTransitions`. Logical simulation only: no OS input,
and Console/Terminal/SDL native-event parity is a later qualification.

Scenario metadata: `Kind` (`dialog`, `window`, `menu`, `status`, `screen`, `mixed`), `Grid(minW, minH, maxW, maxH)` plus
default grids (constructor `sizes`, default 80x25 and 140x40), `Metadata(key, value)` (application-owned, bounded),
`Conditions(text)`, `Safety(ScenarioSafety)`, `ExpectFailure()` (deliberate failure demonstrations; excluded from bulk
selection). Hooks: `Setup` (UI thread, after the application exists, before the loop), `Ready` (before Start),
`Completion` (cleanup verification, null = verified), `Cleanup` (after the loop, on success, failure, cancellation or
controller loss), and on the definition `Ready` and `Teardown`.

`TestTargetContext`: application, `NullDriver`, current and initial grid, the controller-owned empty working directory
(process CWD) and `StateDirectory`, `TopView`/`NoModalView`, and `Identify(view, id)` for stable diagnostic IDs.

### Safety and fixtures

Every execution runs in a fresh process with fresh empty `fixture` (CWD) and `state` directories under
`%TEMP%/tsharp-diagnostics-<32 hex>/` with an ownership marker; the fixture is fingerprinted after setup and after exit;
the root is deleted after link checks. Inherited `TSHARPVISION_*` variables are stripped; `HOME`, `XDG_*`, `APPDATA` and
`LOCALAPPDATA` point below the root's `profile` directory (best effort: Windows known-folder APIs are unaffected, so
applications must use `StateDirectory`). The target is started with the controller's own token (never elevated).

`ScenarioSafety(FixtureFiles, Network, ExternalProcesses, Devices, Elevation, Notes)` is a **declaration, not a sandbox**.
The controller refuses scenarios declaring network/processes/devices unless the run allows them (`--allow`) and always
refuses elevation. Real protection must come from the application's fakes, fixtures and refusal seams; arbitrary .NET
code in a target can still start processes or open devices.

### Input acknowledgement

The host installs a testing-side input tracker (`TestInputDriver`) as the display driver in front of the `NullDriver`.
Host keys wait in its FIFO with a sequence number and are handed out by `ReadKeyEvent`, the call `TScreen.GetEvent` makes
for every driver after the framework queue. It records which key the loop read and counts empty keyboard probes (which
happen only when no pending event and no queued framework event exists). Posted probes (delivered by every loop at the
start of a pass, before input) then establish:

| Acknowledgement | Meaning |
|---|---|
| `settled` | the input was taken (key read / action started / resize applied) and afterwards the loop reached an empty probe with no foreign post pending — the key's dispatch, its put-back and queued follow-up events and the following `Idle` (status line update) have run |
| `dispatched` | taken, control returned to an event loop, but quiescence was not observed within the operation limit (for example a continuously posting application) |
| rejected `Timeout: …` | never taken within the limit |

Neither proves application state: checkpoint expectations and assertions do. Events produced later by background
producers (timers, I/O) are application state. No delay is ever used as proof.

## Semantic inspection

`Inspectors.Capture(app, cells, evidence)` (UI thread) runs the requested read-only inspectors; failures are recorded in
`CheckpointInspection.Errors` (never a silent success). All use public framework members only and produce bounded,
deterministic, JSON-ready records:

- `ViewTreeInspector` — paths (front-to-back child indices), type, nearest framework `Kind`, owner-relative `Bounds`,
  absolute `Screen`, clipped `Visible`, flags (visible, selectable, current, selected, active, focused, disabled, modal,
  exposed, shadow), help context, model `Text` where public (button title, input data, window title, cluster, list),
  observed `CellText` for leaf views, `ModalPath`, `FocusedPath`, working area. Limits: 1000 nodes, depth 24.
- `MenuInspector` — the bar and every executing drop-down/popup/submenu (captured inside their modal loops), parent item,
  selection, entries with text, hot char, command, enabled/disabled, accelerator label, key, item rectangle, observed cell
  match and attribute.
- `StatusInspector` — active definition and help context, items with key/command/enabled, model layout (`Drawn`) and
  observed cell match, hint and its observed state (`match`, `truncated`, `not-drawn`), observed row.

## Assertions

Assertions are data (`AssertionSpec`, built with `UiAssert`) evaluated by the controller against the captured cells and
evidence (`AssertionEvaluator`), so results are reproducible from the artifacts. Kinds: view exists/absent/visible/focused,
modal view (or none), within screen, within a view or the working area, not clipped, aligned edges, spacing between
views (application-supplied min..max), disjoint, shadow (application-supplied attribute: the framework's is private),
menu depth, menu selection (model and highlighted cells), menu entry enabled/disabled, submenu open, status item
visible/absent/enabled/disabled, status context, status hint, text at cells, screen contains, attribute at cells.

Results: stable id, `PASS`/`FAIL`/`UNSUPPORTED`/`SKIPPED`, severity (`error`, `warning`), expected, actual, explanation,
scenario, checkpoint, grid, location. Missing or failed evidence is `UNSUPPORTED`; an error-severity non-PASS fails the
scenario (`Failed`). Design rules (spacing, alignment) are application policy; the library only checks supplied
constraints. `Geometry` exposes the rectangle primitives.

## Protocol v1

Local named pipe, one controller, little-endian int32 length + JSON, maximum 4 MiB.

Request: `Version`, `Schema`, `Instance`, `Id`, `Secret`, `Operation`, `Scenario`, `Checkpoint`,
`Input {Kind: key|action|resize|key-down|key-up|text|modifier|mouse|release, Name}` (kinds are additive inside the schema; an earlier controller refuses a catalog that uses them), `Evidence [view-tree|menu|status]`.
Reply: `Id`, `State`, `Schema`, `Instance`, `Reason`, `ProtocolVersion`, `Target` (identity), `Scenarios`, `Capture`
(cells, current grid, observations, time, surface, inspection), `Interaction` (kind, name, routing, sequence,
acknowledgement).

Operations: `Handshake → Ready`, `Start → Started`, `Input → Routed`, `Checkpoint → Captured`, `Complete → Completed`,
`Shutdown → ShutdownAccepted`; failures reply `Rejected` with a reason (`Timeout: ` prefix for bounded waits).

`Protocol.Schema` (`tsharpvision.testing/v1+checkpoints.2`) is mandatory on both sides; binaries built against the POC or
the (`…checkpoints.1`) contract are refused in both directions. There is no negotiation.

Activation: `--tsharpvision-test-target` plus `TSHARPVISION_TESTING_INSTANCE`, `_SECRET` (cleared on read), `_SCENARIO`
(absent = discovery only), `_SIZE` (WxH within the scenario range), `_STATE`, optional `_OPERATION_MS` (100..30000,
default 10000) and `_LIFETIME_S` (5..600, default 40). Exit codes: 0 clean shutdown after Complete, 1 failure/abandoned,
2 refused activation. Controller EOF ends the session: the host quits the application, which unwinds nested loops through
`cmCancel`; the controller kills only its own child after a 3 s grace.

Security: random instance and 256-bit secret per process, fixed-time comparison, unique request IDs, Windows DACL
restricted to the current user with remote clients rejected (`CurrentUserOnly` Unix sockets elsewhere), registered
scenario/key/action names only. Diagnostic authentication, not an OS sandbox.

## Output

Each run writes one flat directory `run-<UTC time>-<id>/` (never reads earlier runs; delete at any time):

```
<scenario>--<checkpoint>--<WxH>.png     logical rendering of the cells
<scenario>--<checkpoint>--<WxH>.tvs1    TVS1 cells (canonical Layer A capture)
<scenario>--<checkpoint>--<WxH>.json    checkpoint metadata, inspection, assertions, provenance
run.json                                options, environment, identity, layers, results, orphan sweep
report.md                               human-readable summary
```

`WxH` is the checkpoint's grid; after an in-scenario resize it is `WxH-from-IxJ` (initial grid), so executions started
at different grids never collide. Files are created with `CreateNew`. Provenance: application assembly/version/MVID,
TSharpVision and Testing versions, schema, OS/architecture/runtime, elevation, driver, capture layer (A captured; B and C
not captured), renderer identity, grid, scenario/checkpoint, capture time, input routing and acknowledgements, SHA-256
hashes, assertion results, review status `Unreviewed`. No secrets, user names or machine names are written.

Before a run the controller sweeps abandoned temp roots conservatively: exact name pattern, valid marker of this tool,
older than one hour, owner process gone (PID + start time), no links, only the expected entries.

## Visual baselines

Opt-in (`RunOptions.Baselines` / `run --baselines <manifest>`); without it a run is byte-for-byte the run. The
application owns a manifest (`tsharpvision.visual-baselines/1`) that names the gated checkpoints by scenario ID,
checkpoint and grid key (`WxH`, or `WxH-from-IxJ` after an in-scenario resize) and the normalization rules each uses;
approved baselines live in `approved/` beside it.

- **Unit of comparison**: one TVS1 cell = one UTF-16 code unit (ordinal, never normalized) + the raw attribute byte
  (foreground low nibble, background high nibble). No tolerance. Dimensions must match. TVS1 is the canonical format;
  the baseline cells are a TVS1 file and nothing else is compared (PNGs are review aids).
- **Normalization** (`run-fixture-id`, `pattern`): a rule declares regions (negative = from the far edge, width/height
  0 = to the edge, optional per-grid), a regex whose named group (`id` / `dynamic`) marks dynamic cells, a match-count
  range and `maxCells`. Only the GLYPHS of those cells become U+FFFD; attributes and every other cell stay exact.
  `run-fixture-id` captures must be a tail of this execution's controller fixture identifier
  (`tsharp-diagnostics-<32 hex>`). Regions above 25% of the screen, masks above 5%, a wrong match count, a capture that
  fails validation, or a rule without a region for the grid is a `NormalizationError`, never a silent mask. The masked
  positions must equal the baseline's.
- **Statuses**: `Match`, `Mismatch`, `BaselineMissing` (never a pass), `BaselineInvalid` (corrupt per SHA-256, wrong
  identity, unsupported format, or a rule fingerprint that changed since approval), `NormalizationError`, `NotCaptured`,
  `NotRun` (informational). Exit code 3 = every scenario passed, the visual gate did not.
- **Artifacts** (flat, `CreateNew`): `<stem>.diff.json` per gated checkpoint; `<stem>.diff.png` on a mismatch (expected |
  actual | map: red glyph, yellow attribute, blue masked); `<stem>.candidate.tvs1/.png/.json` (unreviewed) on anything
  but a match; `visual.json`; a report.md section. The source capture is always written first.
- **Governance**: the runner only reads the store. `baseline approve --baselines <m> --run <dir> --candidate <key,...>
  --confirm approve-<n> [--replace]` is the only writer: explicit keys, manifest entries only, intact candidates made
  under the current rules, and a replacement only of exactly the baseline the candidate was compared against. No Git.
- **Explicit identity mapping**: an entry may add `"executionScenario": "<external scenario ID>"`. `scenario`
  stays the baseline identity (approved key, `scenarioId` in candidate and approved metadata); the entry then gates only
  the run of that external scenario, and only while its validated fixture binding (fixture ID + contract fingerprint from
  the merge, re-verified at launch) names `scenario` as its fixture. Otherwise the run is refused before it starts
  (`Baseline identity mapping ... refused`) and the gate reports `BaselineInvalid`. Baseline identity and execution
  identity must be one-to-one across the manifest (conflicting or ambiguous mappings, or `executionScenario` equal to
  `scenario`, are manifest errors). Without the field nothing changes; an unmapped entry of a fixture identity is never
  borrowed by a bound external scenario (`BaselineInvalid`: "never borrowed implicitly"). Run artifacts keep the
  execution ID; candidate files are named by the baseline key and record `executionScenarioId` (approved metadata keeps
  it; existing approved files without it stay valid unchanged). `baseline status --baselines <m> [--project <p> |
  --target <t>]` lists the mappings and verifies their bindings.

## Logical renderer v2

`LogicalPng.cs` contains original redistributable bitmap glyphs (MIT, `../../LICENSE`); no third-party font data or
operating-system font. Cell 8×12 px; 5×7 Latin glyphs, Unicode Latin marks (Czech included), box drawing, shades, blocks
and, since v2 (`diagnostics-original-bitmap-v2`), the standard symbols `► ◄ ▶ ◀ ● ○ • ◘ □ ▪ √ ✓ → ← ↓ ↔`. Other
characters use a visible outline replacement box. Fixed VGA palette; foreground = low nibble, background = high nibble;
cursor omitted; RGB8 PNG without timestamps. Identical cells give identical pixels everywhere (`pixelsSha256`, the hash of
the uncompressed raster); the compressed PNG bytes (`pngSha256`) are stable per platform runtime only, because deflate
output differs between Windows and Linux. PNGs are only comparable within one renderer identity.

TVS1 is `TVS1` + little-endian int32 width/height + row-major cells (UTF-16LE code unit, raw attribute byte); grids are
bounded to 20x6..200x100 by the Workbench (1..200 × 1..100 by the format).

The capture layer is **logical** (Layer A) with **NullDriver**. These pixels are not SDL, console or terminal output and
are never labelled as a native screenshot.


## Real Commander Console execution

Run Configuration (F5) > Rendering & Drivers > Native driver > Windows Console uses the real
Win32ConsoleDriver and real Commander event loop in a private classic conhost. The existing authenticated
pipe, isolated CommanderSession, assertions, modeless Running Tests/Results and flat run.json/report.md
remain authoritative. `--driver Win32ConsoleDriver` is also accepted by the CLI and maintained Commander launcher.
Only F10 open/close, nested submenu, Window > Jobs status transition, and Copy/Rename here cancellation are eligible. Mouse, resize, held/text
input, arbitrary actions remain unavailable; GPU modes are documented below. Unsupported marked/group selections are refused before
launch; All eligible explicitly excludes unsupported scenarios. Native baselines are never approved here.
An external JSON scenario is eligible through the fixture of its validated binding (diagnostic backend only), so
the Commander documents `external/commander/...` bound to those audited fixtures run natively under their external IDs;
their own steps must still be audited keys, left clicks (not on the console host) and checkpoints. Ordinary Proxy
scenarios and unbound documents are never eligible. The runner states the refusal (`NativeExecution.Refusal`).

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


## External JSON scenarios

Version-1 `.twproj` optionally contains `scenarioFiles`: explicit project-relative JSON paths. Compiled fixtures and external identities coexist; CLI `run project.twproj --scenario <id>` and the existing TUI use the same loader and runner. See [declarative scenarios](../../docs/scenarios.md) for the schema, action/assertion contract, retry behavior, baseline references and project exit codes.
