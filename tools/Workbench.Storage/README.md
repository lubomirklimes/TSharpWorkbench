# Native project storage CLI

This independent harness exercises the production `.tsw` storage API without switching the legacy TUI or runner.

```powershell
dotnet run --project tools/Workbench.Storage -- create C:\Projects\Example.tsw "Example"
dotnet run --project tools/Workbench.Storage -- attach C:\Projects\Example.tsw attachments/screenshot.png C:\Captures\screen.png
dotnet run --project tools/Workbench.Storage -- rename C:\Projects\Example.tsw "Renamed"
dotnet run --project tools/Workbench.Storage -- inspect C:\Projects\Example.tsw
```

The destination directory must exist. An interrupted session exits with code 3 until an explicit `--recover` or `--discard` choice is supplied. `inspect --recover` reads recovered data but does not save it; a later mutating command with `--recover` can commit it. Discard quarantines the workspace and reopens the saved archive. If no archive was ever saved, Discard quarantines the workspace and open fails; `create` may then be used explicitly. No command imports or runs scenarios. Use `ProjectSession.PutScenario`, `PutEnvironment`, and `PutTest` for typed metadata; an attachment alone does not register a scenario.

`Dispose` always preserves recovery state. API callers should explicitly `Close(Save)` after edits, `Close(Discard)` to quarantine them, or `Close(Cancel)` to keep the session open. Retained `.lock` files are harmless; the open OS handle controls ownership. Do not delete locks to force an active session open. Working data defaults to LocalApplicationData/TSharpWorkbench/Storage and must not be auto-cleaned. Keep that root stable across launches.
`--workspace-root <path>` overrides the durable root for controlled tooling/tests; use the same root on every subsequent command for that project.
