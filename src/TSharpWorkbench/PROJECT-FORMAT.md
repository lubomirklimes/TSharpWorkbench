# TestWorkbench project format, version 1

A single active project is a UTF-8 JSON file with extension `.twproj`. The file is configuration, not a result archive.
Opening a project validates its schema, resolves paths against its containing directory, and discovers the target's
current catalog. Discovery does not execute a scenario or construct the tested application's UI.

```json
{
  "schema": "TSharpVision.TestWorkbench",
  "version": 1,
  "name": "SampleTarget",
  "target": "../../tests/TSharpWorkbench.SampleTarget/bin/Debug/net10.0/TSharpWorkbench.SampleTarget.exe",
  "outputRoot": "../../artifacts/project-runs",
  "selectedIds": ["sample/status/context"],
  "group": "status",
  "search": "",
  "collapsedGroups": ["selftest"],
  "mode": "NullDriver",
  "runScope": "selected",
  "testType": "functional",
  "visualBehavior": "compare",
  "screenSizes": ["80x25"],
  "customWidth": 100,
  "customHeight": 30,
  "baselineStore": null,
  "inspect": "declared",
  "allow": [],
  "timeout": null,
  "scenarioTimeout": null
}
```

Paths in this example are relative to this diagnostic project directory. Use the platform's executable name when
editing a project for another platform. Save and Save As recompute portable references from the destination directory;
different volumes may require absolute references. The process working directory is never changed.

`schema` and `version` are mandatory and checked before deserialization. Unsupported versions, unknown properties,
malformed JSON, invalid mode/scope/inspection/capabilities, nonpositive timeouts, null collections and invalid grid
dimensions are rejected. Other omitted fields take the documented defaults shown above (`name` defaults to
`Untitled project`, `target` to empty, `outputRoot` to `runs`, and `collapsedGroups`/`selectedIds` to empty arrays).
An empty target permits saving an unfinished project; an absent executable produces a recovery message on open.

`mode`: `NullDriver`, `SDLDriver`, `Win32ConsoleDriver`, `SDLGpuDriver`, `SDLGpuDriver-D3D12Fallback` or `SDLGpuDriver-Vulkan`. Logical visual comparison uses the optional approved TVS1 baseline manifest.
SDL is the audited four-scenario Commander subset. Console uses an explicitly owned classic conhost and the four keyboard-only scenarios (F10, nested submenu, Window > Jobs, Copy/Rename cancellation); GPU uses the same audited SDL subset; D3D12 shader/fallback qualified, Vulkan F10 smoke qualified. Mode values
do not confer capability on arbitrary targets or scenarios. The existing runner remains the authority.

`testType`: `functional` (default), `visual`, or `rendering`. `visualBehavior`: `compare` (default) or `candidates`.
These optional version-1 fields preserve older valid projects. When `testType` is absent on load, existing native mode implies rendering and an existing logical baseline store implies visual; otherwise it defaults to functional. Functional always uses NullDriver without visual gating;
visual uses NullDriver plus the configured manifest. Candidate mode retains logical comparisons and additionally saves
review copies of canonical matches. Rendering exposes a compact Driver row and a separate Select dialog for the target-supported Windows
SDL software, private Console, D3D12 shader, D3D12 fallback and Vulkan shader paths. Console captures its host
window; SDL/GPU capture renderer targets (GPU rerenders the submitted application snapshot, never the swapchain).
Optional `preferredNativeMode` remembers a chosen native backend across Functional/Visual runs; omitted values
use the saved native `mode` or the first offered path. Unsupported values are refused without fallback.

`runScope`: `selected`, `group` or `all`. F5 opens the modal Run Configuration dialog. The same dialog opens from Test > Run.
The run fields (`mode`, `proxyBackend`, `proxyCapture`, `preferredNativeMode`, `runScope`, `testType`, `visualBehavior`,
`screenSizes`, `customWidth`, `customHeight`, `inspect`, `timeout`, `scenarioTimeout`) are the project's run defaults.
The TUI copies them into a session run configuration when the project opens; running never changes them unless
"Save these settings as project defaults" is ticked in Run Configuration. The CLI uses the saved values.
`all` in native mode explicitly selects only qualified scenarios; marked/group native selections containing unsupported
IDs are refused with counts. A supported-native filter changes visibility without clearing any marks.
`screenSizes` is an ordered, nonempty list of concrete grids. Both standard grids use `["80x25", "140x40"]`.
The saved custom width/height are independent from the Workbench host window. The workspace also preserves the
existing comma-separated custom-grid editor; its first grid supplies `customWidth`/`customHeight` for the Run dialog. Older multiple-custom-grid projects retain additional saved grids until the custom dimensions change.
Dimensions use `ScenarioSelection.ParseSize` and the existing `GridLimits`, not new Workbench limits. The TUI refuses incompatible scenario/grid combinations before launch, explaining counts and supported ranges. The CLI retains the runner's Blocked behavior.

`inspect`: `declared`, `all`, or `none`. `allow` contains only existing runner capability names (`network`, `processes`,
`devices`, `elevation`). Operation timeout is milliseconds (100..30000); scenario timeout is seconds (5..600). Null keeps the runner default.
Results, screenshots, resolved session fixture paths, IPC
tokens and credentials have no fields in this format. Baseline approval is outside project save.

The tree is derived from current catalog slash prefixes. Counts include descendants. `group`, `search` and
`collapsedGroups` are still read and are written by an explicit save (compatibility), but since the redesign they are user view
state: the Workbench keeps the current values per project path in its user settings, restores them from there when
present, and changing them never marks the project as changed. Marks are independent stable scenario IDs. Missing IDs
are removed after discovery and reported on the status line and in Window > Messages (Alt+6); this reconciliation
leaves the project dirty.

Writes use a sibling temporary file followed by replacement. Dirty state clears only after successful replacement.
Closing/replacing/exiting asks Save / Discard / Cancel (Cancel is the default) if project data changed: properties,
marked scenarios or run defaults. Changing the target in Project Properties states before OK that marks will be
cleared. Close while work is active refuses closure; cancellation and quit wait for runner cleanup.

Run acceptance changes project configuration and its dirty state; Project Save remains separate. Cancel discards the
dialog's edit copy, including Options changes. Each launch freezes target/build identity, IDs, scope, mode/driver, grids,
baseline policy, output and advanced options. Browsing cannot alter it. Only one batch can be active. Results and Running
Tests are modeless TWindows; project replacement/closure waits for cancellation and owned cleanup.

Completed runs retain existing run.json/report.md files. A Workbench-only workbench-request.json sidecar records the
effective request without secrets or temporary fixtures. Historical loading validates the existing results/artifacts;
older visual runs without that sidecar retain statuses but cannot recover the full external manifest path automatically.
Repeat Last Run is not offered; reopening Run remembers configuration, then freezes a fresh selection on acceptance.


## External JSON scenarios

Version-1 `.twproj` optionally contains `scenarioFiles`: explicit project-relative JSON paths. Compiled fixtures and external identities coexist; CLI `run project.twproj --scenario <id>` and the existing TUI use the same loader and runner. See [declarative scenarios](../../docs/scenarios.md) for the schema, action/assertion contract, retry behavior, baseline references and project exit codes.

Since the TUI edits this list through the Scenarios menu (Add files, Add folder, Remove scenario, New
scenario); the format is unchanged. The list keeps its order, changing it marks the project as changed, and Save / Save as
write the references relative to the project file (absolute when on another volume). Documents are never copied,
moved or rewritten. The TUI loads each listed document separately and reports a missing or invalid one; the CLI stays
strict.

## Proxy logical sessions

For ordinary compatible applications, reference external documents with `backend: "proxy-logical"`
and no compiled `fixture`. Keep `mode: "NullDriver"` to select logical evidence. Optional
`proxyBackend: "NullDriver"` (default), `"SDLDriver"`, `"SDLGpuDriver"` or `"Win32ConsoleDriver"`
selects the requested decorated backend. On Windows, Console uses an isolated new console with owned process-job cleanup.
Handshake and checkpoint metadata include actual native buffer/window geometry. Optional `proxyCapture` defaults to `"none"`. Supported selections are `"renderer-pixels"` for SDL, `"gpu-completed"` for the active shader GPU path, and `"native-console-cells"` for owned Windows Console sessions. GPU renderer-pixel requests may also observe the actual GPU renderer path. Handshake verifies the actual available kinds; unsupported requests fail without fallback. Logical artifacts remain present beside separately classified native artifacts. See [native capture](../../docs/capture.md#proxy-capture-selection).
The executable in `target` is reused across these documents. CLI and TUI both preserve this
setting and execute through ScenarioRunner. Mixed proxy/compiled document projects are rejected.
Deployment and backend grid limits are described in [proxy sessions](../../docs/architecture.md#proxy-sessions).

## Ordinary target launch configuration

Optional `targetArguments` (array, default empty) and `targetEnvironment` (string dictionary, default empty)
are passed directly to the owned child by both CLI project execution and accepted TUI requests.
There is no shell parsing. `{targetDir}`, `{fixture}` and `{state}` expand at launch to the executable
folder, disposable fixture and isolated state directory. Other values remain literal. Do not put secrets
in project files: these settings are retained in project/request configuration. Framework `TSHARPVISION_*`
variables and HOME/APPDATA/LOCALAPPDATA/XDG profile variables remain session-owned and cannot be replaced.
Windows owned-console launch uses the same arguments with Windows CRT quoting, preserving job ownership.

Optional `proxyShutdownKey` is null by default. The only accepted opt-in is `kbEnter`: the proxy plans
that ordinary input with InputPlanner and queues it immediately before existing cmQuit routing, so a
clean application's nested quit-confirmation loop consumes it. It does not bypass the application's
handler. Use only for a known clean test session whose quit dialog has an affirmative Enter default;
unsaved documents/background jobs require a different scenario and are not covered by this shortcut.
A refused shutdown still times out and uses bounded owned-process cleanup.
See `examples/TSharpCommander/commander.twproj` for per-run profile isolation with this contract.
