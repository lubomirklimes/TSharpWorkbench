# Ordinary proxy session qualification target

This deterministic counter uses only TSharpVision. The ACTIVE_DRIVER stdout line is emitted only
in --smoke mode, so ordinary console execution does not overwrite an already drawn application UI. F2 updates the label through the application's
real HandleEvent. F8 exits abruptly with code 23; F6 blocks for two seconds and F7 for ten seconds
for bounded failure qualification. `--smoke` queues ordinary quit for standalone launch verification.
F3 opens an ordinary modal dialog; Esc closes it. The event telemetry shows the kinds retrieved
through the application event loop, including nested loops. There is no Testing/Workbench reference, TestTargetHost or scenario registration. The same DLL/MVID
is used standalone and under control; tests verify its bytes and deps.json.

Package mode (default) needs no TSharpVision checkout. One command restores, builds and
deploys Workbench, Proxy and this target, then runs `proxy/counter`:

```powershell
./scripts/prepare-proxy-ordinary.ps1 -Run
```

For the Proxy test project in package mode, build `TSharpWorkbench.slnx` with
`--artifacts-path <root>`, assemble the target from `<root>/bin/ProxyOrdinary/release` plus
`TSharpWorkbench.Proxy.dll`, `TSharpWorkbench.Testing.dll`, `TSharpVision.Drivers.SDL.dll`, `SDL3-CS.dll` and
`runtimes/win-x64/native/*.dll` from `<root>/bin/TSharpWorkbench.Proxy/release` and
`TSharpVision.Drivers.Console.dll` from `<root>/bin/TSharpWorkbench/release` (what the script does), and run
`dotnet test tests/TSharpWorkbench.Proxy.Tests/TSharpWorkbench.Proxy.Tests.csproj -c Release --no-build --artifacts-path <root>`
with the environment below. `TSWB_PROXY_TARGET` must be this ProxyOrdinary deployment, never TVDemo.

NuGet build workflow, from the Workbench repository root, PowerShell:

```powershell
$buildArgs = @('-c','Release','-m:1','-p:NuGetAudit=false')
$deployment = Join-Path $PWD 'artifacts/proxy-ordinary/20261024/target'
dotnet build diagnostics/ProxyOrdinary/ProxyOrdinary.csproj @buildArgs -o $deployment
dotnet build tests/TSharpWorkbench.Proxy.Tests/TSharpWorkbench.Proxy.Tests.csproj @buildArgs
dotnet build tests/TSharpWorkbench.Tests/TSharpWorkbench.Tests.csproj @buildArgs
$proxyBin = Join-Path $PWD 'tests/TSharpWorkbench.Proxy.Tests/bin/Release/net10.0'
'TSharpWorkbench.Proxy.dll','TSharpWorkbench.Testing.dll','TSharpVision.Drivers.SDL.dll','SDL3-CS.dll' |
  ForEach-Object { Copy-Item -LiteralPath (Join-Path $proxyBin $_) -Destination $deployment }
Copy-Item "$proxyBin/runtimes/win-x64/native/*.dll" -Destination $deployment
# Run-specific generated evidence and runtime temporary files:
$run = Join-Path $PWD ('TestResults/proxy-ordinary/manual-' + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Force -Path $run,(Join-Path $run 'temp') | Out-Null
$env:TEMP = Join-Path $run 'temp'; $env:TMP = $env:TEMP
$env:TSWB_PROXY_TARGET = Join-Path $deployment 'ProxyOrdinary.exe'
$env:TSWB_PROXY_SCENARIO = Join-Path $PWD 'diagnostics/ProxyOrdinary/counter.scenario.json'
$env:TSWB_PROXY_RESULTS = $run
$env:TSHARPVISION_DRIVER = 'NullDriver'
& $env:TSWB_PROXY_TARGET --smoke
Remove-Item Env:TSHARPVISION_DRIVER
$cli = Join-Path $PWD 'src/TSharpWorkbench/bin/Release/net10.0/TSharpWorkbench.exe'
& $cli run diagnostics/ProxyOrdinary/counter.twproj --target $env:TSWB_PROXY_TARGET --size 80x25,140x40
# Deliberate assertion failure: exit 7; five captures, one input action.
& $cli run diagnostics/ProxyOrdinary/counter.twproj --target $env:TSWB_PROXY_TARGET --scenario proxy/counter-negative
# Explicit real SDL run; evidence remains logical. Both standard grids now work.
& $cli run diagnostics/ProxyOrdinary/counter.twproj --target $env:TSWB_PROXY_TARGET --proxy-backend SDLDriver --size 80x25,140x40
$env:TSW_PROXY_SDL_TEST = '1'
Remove-Item Env:TSHARPVISION_NO_SDL -ErrorAction SilentlyContinue
dotnet test tests/TSharpWorkbench.Proxy.Tests/TSharpWorkbench.Proxy.Tests.csproj -c Release --no-build --no-restore --results-directory $run --logger 'trx;LogFileName=proxy.trx'
```

Use a fresh run directory for each test invocation. In the TUI, open `counter.twproj`, select the
positive counter and run. The project reuses one ordinary executable for positive/negative external
documents. Set its `proxyBackend` field to SDLDriver or SDLGpuDriver and screenSizes to the requested grids
for an explicit SDL project. Native capture providers remain unsupported for this mode.
See [proxy sessions](../../docs/architecture.md#proxy-sessions) and
[capture capabilities](../../docs/capture.md#proxy-capture-selection) for the current execution and evidence boundaries.
