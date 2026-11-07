# Packaging and automation

## CI and release events

`main` is the canonical branch. [CI](../.github/workflows/ci.yml) runs on pushes to `main`, pull requests targeting `main`, and manual dispatch. CI has read-only repository permissions and never publishes a GitHub Release.

[Application release](../.github/workflows/release.yml) runs on `v*` tags and manual dispatch. A manual candidate must be dispatched from `main` and never publishes. A tag candidate must match the effective application MSBuild `Version` exactly (`vX.Y.Z` or `vX.Y.Z-prerelease`). The central application version is `1.2.0-preview.1` in `Directory.Build.props`. Its matching tag is `v1.2.0-preview.1`, and the GitHub Release is marked prerelease. Change that one `Version` value to `1.2.0-preview.2` for the next preview or `1.2.0` for stable; tag/version validation and the prerelease flag follow automatically. There is no auto-bump. Assembly identity stays `1.0.0.0` within Workbench 1.x, file version is `1.2.0.0`, and informational version includes the product preview plus source revision. TSharpVision NuGet dependencies remain independently pinned to stable `1.2.0` in `Directory.Packages.props`.

A release commit must be an ancestor of the fetched `origin/main` tip; it need not equal the current tip. Validation requires the checked-out commit to equal the triggering SHA and, for tags, the tag's peeled commit. Non-main manual refs, unrelated tags, missing history and mismatched versions fail. A tag build keeps the tagged commit; it does not switch to main.

## Native build and package matrix

| Runner | RID | Archive | Default launcher |
| --- | --- | --- | --- |
| `windows-2025` | `win-x64` | ZIP | `workbench.cmd` / Win32ConsoleDriver |
| `ubuntu-24.04` | `linux-x64` | tar.gz | `./workbench.sh` / AnsiTerminalDriver |
| `macos-15-intel` | `osx-x64` | tar.gz | `./workbench.sh` / AnsiTerminalDriver |
| `macos-15` (Apple Silicon) | `osx-arm64` | tar.gz | `./workbench.sh` / AnsiTerminalDriver |

Runner labels are explicit OS versions from the [GitHub runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners). Linux ARM64 is not a release target yet. Both workflows use PowerShell 7, setup-dotnet for stable .NET 10, and `global.json` (10.0.100 minimum with latest-feature roll-forward).

Each job performs a locked portable restore, Release compilation, bounded headless and payload-integrity selections, packaging, extraction and CLI smoke. A selected test may not silently skip: the TRX must be nonempty and contain only passing executed cases. Hung test hosts have a two-minute bound. The selections cover recording consumption, checkpoint detection/planning, inspection, preferences/artifact names, framework lifecycle, major-version compatibility and payload manifest/tamper validation. Required payload tests fail if their input is missing rather than returning early. It does not claim full-suite coverage or native/visible-window qualification.

The declared test stack is Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3 and the Visual Studio adapter 3.1.4. Jobs use that VSTest adapter with .NET 10; the first hosted run must confirm Linux/macOS test-host compatibility. Headless selection does not load SDL/conhost providers and installs no graphical system packages. SDL/GPU diagnostics remain explicit opt-in checks on a suitably provisioned interactive host, with platform display/windowing and graphics dependencies installed there.

Checked-in portable NuGet locks are used in locked mode. RID expansion is isolated in ignored per-project locks under `artifacts/github/rid-locks/<rid>`; the portable dependency graph must stay identical, then the RID graph is restored again in locked mode. SDK/apphost RID assets are resolved from the declared package sources. No checked-in lock is rewritten. Expanded locks are uploaded as evidence; these native graphs still need first hosted qualification on non-Windows runners.

## Distribution contract and launch

Artifacts are **framework-dependent**, not self-contained. Install the matching architecture's **.NET 10 runtime** and its [OS prerequisites](https://learn.microsoft.com/dotnet/core/install/) before launch; the SDK is only needed for building. An ASP.NET runtime is not required. A macOS ARM64 package needs the ARM64 runtime, while an Intel package needs the x64 runtime.

Keep the entire extracted directory: apphost, managed dependencies, runtime configuration, help resources/checksum, license, launcher, and `proxy-payload`. The Proxy manifest must declare package-mode provenance, the intended native RID and matching SHA256 values for every staged file. Foreign native RID directories are rejected. Managed Windows driver assemblies may remain as dependency identities on Unix; Windows native binaries must not be shipped as Unix native assets.

Windows:

```powershell
Expand-Archive TSharpWorkbench-1.2.0-preview.1-win-x64.zip -DestinationPath workbench
./workbench/TSharpWorkbench.exe help
./workbench/workbench.cmd tui
```

Linux/macOS (use the archive matching the OS and CPU):

```sh
mkdir workbench
tar -xzf TSharpWorkbench-1.2.0-preview.1-linux-x64.tar.gz -C workbench
./workbench/TSharpWorkbench help
./workbench/workbench.sh tui
```

Unix tar archives preserve executable modes for both apphost and launcher. Unix launchers select `AnsiTerminalDriver` unless a driver was explicitly set; they require an interactive terminal for the TUI. The Windows launcher defaults to `Win32ConsoleDriver`. CLI dispatch occurs before TUI/driver construction. `help` is the supported noninteractive smoke command.

Each package is extracted again, checked against its manifest and invoked through both apphost and launcher. The in-repository SampleTarget must be built in the same output tree (the CI Build phase does this). The extracted CLI must execute `sample/dialog/basic` through real IPC on NullDriver and record nonzero passing checkpoints and assertions. Missing SampleTarget is an error; help-only smoke is insufficient. Structured batch and summary evidence is retained under `scenario-smoke/`. Unix processes inherit a short private temp root as described in [CI commands](build.md#ci-commands).

Native Win32 Console/conhost diagnostics are Windows-only. ANSI Terminal is the Unix TUI path; headless CLI checks do not qualify interactive TUI behavior. SDL/SDL GPU require a suitable graphical session, platform libraries and hardware; bundled native assets and a successful help command do not establish rendering, physical-input or compositor correctness. See [platform status](../README.md#platform-status) and [capture boundaries](capture.md).

macOS binaries are **not developer-signed or notarized**. Download quarantine/Gatekeeper may therefore prevent normal launch. Verify provenance and checksums and follow your organization's policy for unsigned applications; the launcher does not remove quarantine or disable security checks. Developer ID signing and notarization require separately configured Apple credentials and approval.

## Compatibility policy and one mandatory TVDemo integration

Workbench 1.x targets TSharpVision 1.x. Minor, patch, prerelease/build labels and exact assembly bytes are diagnostic information, not blanket rejection criteria. The application's framework is kept. Real assembly/API loading, IPC protocol negotiation, required capabilities and scenario outcomes still determine whether an operation works. A matching major is eligibility for verification, not a guarantee of every API or backend combination. Other majors, inconsistent major identities, unreadable assemblies, payload tampering and unrelated native/third-party conflicts remain errors.

The required Windows release integration is exactly one **real TVDemo** built from the latest stable `v1.x.y` framework tag and one representative About-dialog scenario. It does not require another same-version/different-build fixture, an obsolete-framework fixture, or a Proxy repair matrix. Independent repair/diagnostic tests remain available outside that mandatory fixture gate.

The validation job queries only `https://github.com/lubomirklimes/TSharpVision.git`, filters out every prerelease and nonmatching major, compares stable versions numerically (so 1.10 exceeds 1.9), and resolves an annotated tag to its peeled immutable commit. It records tag, SHA, discovery UTC, product version and selection policy once per candidate in `fixture-selection.json`. Each native job downloads that same selection; none rediscovers a branch or tag later. Current public selection is `v1.2.0` at `e9e38d5a661a26557823179fba654609ef150be9`; a genuinely newer stable 1.x tag will be selected on a future candidate.

Windows `Fixture` downloads the authoritative source archive by **commit SHA**, records its SHA256, locates the actual unique `TSharpVision.Samples.TVDemo.csproj` in that source, and builds its real project references and dependencies with .NET 10. Source/build outputs stay under ignored candidate artifacts. No developer checkout, fake executable, alternate TVDemo variant or manually installed old framework is required. Source revision is explicitly set to the pinned framework commit rather than inherited from the Workbench parent repository.

`ReleaseGate` prepares a copy of that real application with the current Proxy, then runs [the reviewed scenario](../tests/fixtures/tvdemo-about.scenario.json) through actual local IPC on NullDriver. Keyboard input opens Help/About. The scenario must execute once, report Passed, and produce passing assertions for both **About TVDemo** and **A T# Vision sample application.** Empty results, skipped/missing scenarios, zero assertions, failed preparation and network/build failures fail the gate and record a reason. `fixture.json`, `tvdemo-batch.json`, `tvdemo-results/` and `release-gate.json` preserve the measured result. Windows release manifests embed the integration result; all four platform manifests record the same pinned selection, checked by the publisher.

This integration is Windows/headless logical qualification. It does not claim Linux/macOS TVDemo integration, interactive TUI, native graphics or physical-input qualification. Those platforms retain independent native app packaging and extracted-CLI smoke jobs.

## Local candidate checks

From the repository root using PowerShell 7 and the .NET 10 SDK:

```powershell
./scripts/test-ci-workbench.ps1
./scripts/ci-workbench.ps1 -Phase Version
./scripts/ci-workbench.ps1 -Phase Restore
./scripts/ci-workbench.ps1 -Phase Build
./scripts/ci-workbench.ps1 -Phase Test
# Real TVDemo release integration on Windows:
./scripts/ci-workbench.ps1 -Phase FixtureResolve
./scripts/ci-workbench.ps1 -Phase Fixture
./scripts/ci-workbench.ps1 -Phase ReleaseGate
./scripts/ci-workbench.ps1 -Phase Package
```

RID defaults to the native process OS/CPU and must match it. Supported targets are the four matrix RIDs. `-OutputRoot artifacts/<fresh-name>` keeps independent candidates separate. `Package` refuses an existing publish/extraction directory; `-Phase Smoke` rechecks an already extracted package without republishing. Package builds default to `artifacts/github`; no source checkout outside this repository is required. On an empty hosted candidate, the order is Test → Fixture → ReleaseGate → Package (which extracts and smokes the archive). For local reproduction, pass `-FixtureSelectionFile <saved fixture-selection.json>` to Fixture to build the same immutable commit; downloading/building still requires public source/NuGet access unless already cached. Hosted Fixture requires the selection artifact from the validation job and cannot silently select an override. Use a fresh output root to reprovision source; the default will not overwrite an existing fixture source tree.

## Publishing controls and evidence

Only a successful tagged run can reach the `application-release` protected environment, after **all four** native jobs and the Windows TVDemo integration gate. Configure required reviewers and `v*` deployment-tag restrictions for that environment before release use. Protect `main` and version tags according to repository policy. Ordinary main pushes and manual candidates never publish.

Only the publishing job gets `contents: write`; validation and builds use `contents: read`. Checkout credentials are not persisted. Each platform uploads one uniquely named candidate artifact. The single publishing job downloads all four, verifies every archive/manifest checksum and manifest version/RID/commit, combines checksums into `SHA256SUMS`, refuses an existing release, creates a draft, attaches the complete asset set once, then publishes it. A failed API call is an error, not evidence that a release is absent. An interrupted draft is left for owner inspection.

Asset names are `TSharpWorkbench-<version>-<rid>.zip` (Windows) or `.tar.gz` (Unix), with a same-stem `.manifest.json` and release-wide `SHA256SUMS`. CI/candidate logs, TRX, fixture/gate records, expanded locks and smoke evidence are retained as workflow artifacts for 14 days. Physical UI approval remains an explicit reviewer responsibility.

Initial GitHub-hosted Windows/Linux/macOS validation, repository permissions and protected-environment configuration require owner action on GitHub. Local Windows results do not establish that Linux/macOS hosted jobs passed. No workflow was dispatched and no tag/release was created while implementing these files.
