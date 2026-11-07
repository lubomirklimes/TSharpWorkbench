# Installation

TSharpWorkbench targets .NET 10. Install the .NET 10 SDK to build from source. A framework-dependent published package needs the matching .NET runtime; a self-contained package includes its runtime.

## Build and start

From the repository root:

```sh
dotnet restore src/TSharpWorkbench/TSharpWorkbench.csproj
dotnet build src/TSharpWorkbench/TSharpWorkbench.csproj -c Release
dotnet run --project src/TSharpWorkbench/TSharpWorkbench.csproj -c Release --no-build -- tui
```

Use an interactive terminal for the TUI. The Workbench host display and the target application's selected test driver are separate settings. See [platform status](../README.md#platform-status) for qualification limits.

## First project

1. Choose **Project → New**, select a location for a `.tsw` project, and set the target application in project properties.
2. Open **Settings → Target Diagnostics**. For an ordinary application, prepare a separate target copy when Proxy support is needed.
3. Create or attach a scenario in **Tests → Scenario Manager**, then create a test and assign an environment.
4. Run the test with **F5** and inspect its checkpoints in **Results**.

The [illustrated manual](manual/Manual.md#priklad) walks through a complete first test. Existing `.twproj` JSON configurations remain useful for [command-line runs](usage.md#command-line).

## Preparing an ordinary target

The CLI previews a preparation plan without changing the target:

```sh
TSharpWorkbench prepare path/to/Application.exe
```

Add `--execute` to create the prepared copy. Use the executable path printed by preparation in your test configuration. `--root <directory>` selects the prepared-copy store. `--in-place` instead modifies the original application folder and should be chosen deliberately.

Keep the complete Workbench output directory, including `proxy-payload`, when distributing it. See [build configuration](build.md), [packaging](release.md), and the [Proxy architecture](architecture.md#proxy-sessions).
