# Building from source

The projects target .NET 10. Framework package versions are centrally defined in `Directory.Packages.props`; `NuGet.Config` resolves packages from nuget.org. A separate TSharpVision source checkout is not required.

```sh
dotnet restore src/TSharpWorkbench/TSharpWorkbench.csproj
dotnet build src/TSharpWorkbench/TSharpWorkbench.csproj -c Release
```

The Workbench project builds its Storage, Testing, and Proxy dependencies. Its build targets stage the Proxy payload beside the application. Preserve that directory when copying the build output.

For an isolated output tree, pass `--artifacts-path <directory>` to restore/build commands consistently. Build diagnostic fixture projects separately when using them; the product build does not imply that every fixture has been built.

See [installation](installation.md) for first launch, [repository structure](structure.md) for project responsibilities, and [packaging and automation](release.md) for publishing and CI guidance.

## Targeted test inputs

Set only the inputs needed by the tests you select. Evidence and capture variables name output directories; target variables name prepared executables. Tests requiring an external application need a compatible deployment built separately.

| Variables | Purpose |
| --- | --- |
| `TSWB_PROXY_TARGET`, `TSWB_PROXY_SCENARIO`, `TSWB_PROXY_RESULTS`, `TSWB_PROXY_CONTROLLER` | Ordinary Proxy deployment, scenario JSON, result directory, and optional ConsoleController executable. See the [Proxy fixture workflow](../diagnostics/ProxyOrdinary/README.md). |
| `TSWB_PRODUCT_TARGET`, `TSWB_PROXY_PAYLOAD` | Prepared ordinary recording target and Proxy payload directory. |
| `TSWB_DIAGNOSTIC_TEST_TARGET`, `TSWB_DIAGNOSTIC_TEST_OUTPUT` | Explicit diagnostic Commander TUI smoke target and output directory. |
| `TSWB_FIXTURE_RESULTS`, `TSWB_FIXTURE_COMMANDER_SCENARIOS` | Fixture qualification output directory and external Commander scenario directory. |
| `TSWB_FIXTURE_REVISION`, `TSWB_FIXTURE_CLEANUP_MARKER` | SampleTarget child-process test inputs for contract drift and cleanup verification; normally supplied by the tests themselves. |
| `TSWB_EXTERNAL_DOCUMENT_EVIDENCE` | External scenario freshness evidence. |
| `TSWB_PROJECT_CREATION_EVIDENCE`, `TSWB_SCENARIO_MANAGEMENT_EVIDENCE` | Project creation and Scenario Manager logical evidence. |
| `TSWB_PROJECT_INFORMATION_CAPTURE`, `TSWB_SCENARIOS_MENU_CAPTURE`, `TSWB_START_PAGE_CAPTURE` | Logical captures for Project Information, the Scenarios menu, and the Start page. |
| `TSWB_RECORDER_INTEGRATION_EVIDENCE`, `TSWB_LAYOUT_EVIDENCE` | Recording review integration and layout assertion evidence. |

Capture comparisons retain their `before`/`after` selectors: `TSWC1_PHASE` for Project Information and related editor tests, `TSWSC1_PHASE` for the Scenarios menu, and `TSWUXC1_PHASE` for the Start page. These select evidence states rather than execution backends. Logical captures do not qualify native rendering.

## CI commands

The checked-in `global.json` permits stable .NET 10 feature-band roll-forward. GitHub Actions installs a stable `10.0.x` SDK and uses the same PowerShell 7 entry points as local candidates:

```powershell
./scripts/test-ci-workbench.ps1
./scripts/ci-workbench.ps1 -Phase Restore
./scripts/ci-workbench.ps1 -Phase Build
./scripts/ci-workbench.ps1 -Phase Test
./scripts/ci-workbench.ps1 -Phase Package
```

These commands use ignored `artifacts/github` output and preserve checked-in NuGet locks. Pass `-OutputRoot artifacts/<fresh-name>` for another isolated candidate. See [CI/release events, matrix, release blockers and distribution requirements](release.md).

On Linux/macOS, each CI script invocation creates a private `0700` directory under `/tmp/tsw-<random-id>` and sets `TMPDIR`, `TMP` and `TEMP` before launching .NET, testhost, the Workbench or its targets. This keeps Unix diagnostic socket filenames below macOS limits even when the runner's default temp or checkout path is long. Compiler/session scratch and sockets are the ephemeral exception to checkout-local output; build artifacts and evidence remain under `artifacts/`. The script restores the environment and removes only its marked temporary directory in `finally`. Windows retains the checkout-local temp directory. Extracted-package smoke must run the real SampleTarget scenario and record nonzero passing checkpoints/assertions; it cannot succeed with help-only or missing-target evidence.
