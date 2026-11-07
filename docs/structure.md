# Repository structure

| Directory or project | Responsibility |
| --- | --- |
| `src/TSharpWorkbench` | TUI, CLI, scenario editing, target preparation, results, and project workflows. |
| `src/TSharpWorkbench.Proxy` | Driver decoration and automation support for ordinary target applications. |
| `src/TSharpWorkbench.Testing` | Scenario execution, assertions, inspection, and target-session protocol. |
| `src/TSharpWorkbench.Storage` | Native `.tsw` project persistence. |
| `src/TSharpWorkbench.Diagnostics.*` | Specialized driver, native-host, and pixel diagnostic tools. |
| `tests/` | Automated tests and the diagnostic sample target. |
| `diagnostics/` | Small controlled target applications and console utilities. |
| `tools/` | Supporting help and storage utilities. |
| `docs/` | User guides, illustrated manuals, and media. |

The [architecture guide](architecture.md) explains how the frontend, runner, workspace, and target interact. Start with [building](build.md) to compile the product, or the [manual](manual/Manual.md) to use it.
