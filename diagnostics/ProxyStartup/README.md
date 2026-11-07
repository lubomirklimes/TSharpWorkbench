# Framework-only proxy startup probe

Purpose: run an ordinary TApplication through its real bootstrap/lifecycle without a compile-time Workbench or Testing dependency. It draws, reports the active driver, queues a safe logical quit and exits. No device writes or external mutations.

Inputs: the published TSharpVision package family, writable artifacts run directory and an explicitly selected installed backend. NullDriver is the qualified safe default. Build the proxy separately for optional deployment.

From Workbench root:

~~~powershell
dotnet build diagnostics/ProxyStartup/ProxyStartup.csproj -c Release -m:1 -o artifacts/proxy-startup/<run-id>/startup
$env:TSHARPVISION_DRIVER='NullDriver'
$env:TSHARPVISION_DRIVER_DECORATOR=$null
dotnet artifacts/proxy-startup/<run-id>/startup/ProxyStartup.dll
~~~

Expect ACTIVE_DRIVER=TSharpVision.Drivers.NullDriver and exit 0. Build the proxy per docs/proxy-driver-prototype.md, then copy its TSharpWorkbench.Proxy.dll and TSharpWorkbench.Testing.dll beside the executable. Do not rebuild the probe or edit its deps.json.

~~~powershell
$env:TSHARPVISION_DRIVER_DECORATOR='TSharpWorkbench.Proxy.WorkbenchProxyDriverFactory, TSharpWorkbench.Proxy'
dotnet artifacts/proxy-startup/<run-id>/startup/ProxyStartup.dll
~~~

Expect ACTIVE_DRIVER=TSharpWorkbench.Proxy.WorkbenchProxyDriver and exit 0. This proves startup loading, not arbitrary-process attachment, IPC or native rendering. Capture logs under TestResults/proxy-startup/<run-id>/.
