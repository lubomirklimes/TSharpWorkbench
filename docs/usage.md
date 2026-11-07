# CLI and TUI usage

## Interactive workbench

```sh
TSharpWorkbench tui
TSharpWorkbench tui --project path/to/Project.tsw
```

The Start Page opens or creates a project. Use Scenario Manager to edit scenario documents, Test Explorer to assign scenarios and environments, and Run Explorer to inspect executions. **F5** opens run configuration, **F6** opens Results, **F10** activates the menu, and **Alt+X** exits. **F1** opens help for the active window.

A native `.tsw` project stores project data and managed artifacts. A `.twproj` file is a JSON execution configuration; see the [version-1 format](../src/TSharpWorkbench/PROJECT-FORMAT.md). Follow the [illustrated manual](manual/Manual.md) for project, test, environment, and baseline workflows.

## Command line

Run a saved JSON project configuration and select an explicit scenario:

```sh
TSharpWorkbench run path/to/project.twproj --scenario example/dialog --output runs
```

For a diagnostic target that advertises compiled scenarios:

```sh
TSharpWorkbench list --target path/to/Target.exe
TSharpWorkbench describe --target path/to/Target.exe --scenario sample/dialog/basic
TSharpWorkbench run --target path/to/Target.exe --scenario sample/dialog/basic --size 80x25 --output runs
```

Use IDs actually advertised by your target. Ordinary applications use attached [proxy-logical JSON scenarios](scenarios.md) rather than a compiled scenario catalog. `TSharpWorkbench help` lists command options.

## Results and baselines

A run writes a separate run directory containing structured results, a readable report, and checkpoint evidence. Inspect individual assertions in Results. Logical screen comparisons and native evidence have different capture boundaries; see [capture types](capture.md).

Baseline candidates require review and explicit approval. A passing functional assertion does not approve a visual reference. The [manual](manual/Manual.md#reference) describes baseline comparison and approval.

See [recording](recorder.md), [layout audits](layout.md), and [CI execution](release.md#automation) for focused workflows.
