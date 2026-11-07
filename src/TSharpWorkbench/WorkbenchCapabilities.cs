using TSharpWorkbench.Testing;

// Describes target launch paths independently of the Workbench display driver.
internal sealed record WorkbenchCapability(string Name, bool Executable, string Explanation);
internal static class WorkbenchCapabilities
{
    internal static readonly string[] NativeModes = ["SDLDriver", "Win32ConsoleDriver", ..NativeExecution.GpuModes];
    // Target display drivers of an ordinary application (Proxy) run; the target's own TSharpVision driver.
    internal static readonly string[] ProxyBackends = ["NullDriver", "SDLDriver", "SDLGpuDriver", "Win32ConsoleDriver"];
    internal static readonly WorkbenchCapability[] Modes =
    [
        new("Logical UI — NullDriver", true, "Cell state, simulated input, assertions, TVS1, logical PNG and logical visual baselines."),
        new("SDL 2D — software", OperatingSystem.IsWindows(), "Actual Commander SDLDriver renderer-target readback; audited menu, mouse menu and Copy/Rename cancellation."),
        new("Windows Console — owned conhost", OperatingSystem.IsWindows(), "Actual Commander Win32ConsoleDriver; private console input records and host-window capture. Audited keyboard workflows; mouse unavailable."),
        new("SDL GPU — D3D12 shader", OperatingSystem.IsWindows(), "SDLGpuDriver; qualified on RTX 2060. Actual backend/path/hardware checked every run; unavailable devices fail without substitution."),
        new("SDL GPU — D3D12 CPU fallback", OperatingSystem.IsWindows(), "Qualified production CPU-composite/upload/blit path selected through diagnostic pipeline-construction failure simulation."),
        new("SDL GPU — Vulkan shader", OperatingSystem.IsWindows(), "Focused real-Commander F10 smoke qualified on RTX 2060. Other audited workflows eligible but not individually qualified on Vulkan."),
        new("GPU capture boundary", false, "Submitted application snapshot rerendered on its render worker into readable GPU texture; GPU download + fence. Not swapchain/compositor/monitor readback. Input is programmatic SDL events."),
        new("Driver Output", false, "Separate qualification tools; no application scenario launch path.")
    ];
    internal static void Validate(string driver)
    {
        if (driver != "NullDriver" && !(NativeExecution.IsMode(driver) && OperatingSystem.IsWindows()))
            throw new ArgumentException($"Unsupported test mode '{driver}'. Available on Windows: SDLDriver, Win32ConsoleDriver, SDLGpuDriver, SDLGpuDriver-D3D12Fallback, SDLGpuDriver-Vulkan. No fallback was performed.");
    }
    internal static string Label(string driver) => driver switch
    {
        "SDLDriver" => "SDLDriver/software - renderer-target",
        "Win32ConsoleDriver" => "Win32ConsoleDriver / conhost - host-window",
        "SDLGpuDriver" => "SDL GPU / D3D12 shader",
        "SDLGpuDriver-D3D12Fallback" => "SDL GPU / D3D12 CPU fallback",
        "SDLGpuDriver-Vulkan" => "SDL GPU / Vulkan shader",
        _ => "Logical UI - NullDriver"
    };
    // Plain names of the tested application's display driver (never the Workbench's own window).
    internal static string TargetDriverName(string driver) => driver switch
    {
        "SDLDriver" => "SDL window (SDLDriver)", "SDLGpuDriver" => "SDL GPU window (SDLGpuDriver)",
        "Win32ConsoleDriver" => "Windows console (Win32ConsoleDriver)",
        "SDLGpuDriver-D3D12Fallback" => "SDL GPU window, Direct3D 12 CPU fallback (SDLGpuDriver)",
        "SDLGpuDriver-Vulkan" => "SDL GPU window, Vulkan (SDLGpuDriver)",
        _ => "headless (NullDriver)"
    };
    // Run Configuration rows: plain name padded to a column, then a short technical description.
    internal static string ProxyChoice(string backend) => backend switch
    {
        "SDLDriver" => "SDL ~w~indow      software renderer (SDLDriver)",
        "SDLGpuDriver" => "SDL G~P~U window  Direct3D 12 (SDLGpuDriver)",
        "Win32ConsoleDriver" => "Windows c~o~nsole conhost (Win32ConsoleDriver)",
        _ => "~H~eadless        no window (NullDriver)"
    };
    internal static string NativeChoice(string mode) => mode switch
    {
        "SDLDriver" => "SDL window        software renderer (SDLDriver)",
        "Win32ConsoleDriver" => "Windows console   conhost (Win32ConsoleDriver)",
        "SDLGpuDriver" => "SDL GPU window    Direct3D 12 shader",
        "SDLGpuDriver-D3D12Fallback" => "SDL GPU fallback  Direct3D 12 CPU composite",
        "SDLGpuDriver-Vulkan" => "SDL GPU Vulkan    Vulkan shader",
        _ => mode
    };
    internal static string EvidenceName(string kind) => kind switch
    {
        ProxyCaptureKinds.Renderer => "renderer pixels", ProxyCaptureKinds.Gpu => "GPU completed frame",
        ProxyCaptureKinds.Console => "console cells", _ => "none"
    };
    internal static string EvidenceChoice(string kind) => kind switch
    {
        ProxyCaptureKinds.Renderer => "Renderer pixels      SDL window, SDL GPU window",
        ProxyCaptureKinds.Gpu => "GPU completed frame  SDL GPU window",
        ProxyCaptureKinds.Console => "Console cells        Windows console",
        _ => "None"
    };
    // Why a proxy backend cannot be chosen here, or null when it can.
    internal static string? ProxyUnavailable(string backend) =>
        backend == "Win32ConsoleDriver" && !OperatingSystem.IsWindows() ? "Windows console is available on Windows only." : null;
    internal static IEnumerable<string> Describe() => Modes.SelectMany(m => new[] { m.Name + (m.Executable ? " (available)" : " (informational)"), m.Explanation })
        .Concat(["Workbench display and target driver are independent.", "Logical PNG renders cells, never native pixels."]);
}
