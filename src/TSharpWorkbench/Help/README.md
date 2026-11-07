# Workbench help resource

`TSharpWorkbench.help.json` is the editable UTF-8 authoring source. `TSharpWorkbench.hlp` is a real TSharpVision **FBH2** binary, not JSON with another extension and not Microsoft WinHelp. The companion `.sha256` detects damaged or mixed deployments before deserialization. Both files are distributed in regular builds and publish output; the runtime reads only these files relative to `AppContext.BaseDirectory`. No compiler, SDK or source checkout is required to read help.

Regenerate from the repository root after editing source:

```powershell
dotnet run --project tools/Workbench.HelpCompiler -- src/TSharpWorkbench/Help/TSharpWorkbench.help.json src/TSharpWorkbench/Help/TSharpWorkbench.hlp
```

Commit source, binary and checksum together (the task agent does not stage or commit). The compiler validates unique IDs and link targets, writes UTF-16 paragraphs and cross-references using public TSharpVision 1.1.0 `THelpFile` / `THelpTopic` APIs, and flushes the topic index. The focused resource test compares compiled paragraphs and link IDs with the editable source, detecting stale assets.

Format: little-endian FBH2 magic `0x32484246`, file-size bookkeeping, index offset, serialized THelpTopic objects followed by THelpIndex. Paragraphs wrap to the native viewer width; linked labels use nonbreaking spaces. This is the framework UTF-16 successor to the classic FBHF help format. IDs 1..18 are stable and named in `WorkbenchHelpContext.cs`; Index is 1 as required by the framework viewer. Append new IDs rather than renumbering existing ones.

Tab / Shift+Tab chooses links, Enter follows, Backspace returns, arrows and PgUp/PgDn scroll, Alt+I or F1 opens Index, Esc closes. Search is not exposed by the installed 1.1.0 viewer API, so Workbench adds no separate search implementation. About retains installed version information and attribution; the HLP About topic includes the complete MIT licence.
