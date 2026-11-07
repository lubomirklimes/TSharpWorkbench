namespace TSharpWorkbench.Diagnostics.NativePixels;

/// <summary>What SDL offers on this machine. Probed in its own process so no SDL state leaks into a path run.</summary>
public sealed class BackendInventory
{
    public string SdlVersion { get; set; } = "";
    public List<string> VideoDrivers { get; } = new();
    public List<string> RenderDrivers { get; } = new();
    public List<string> GpuDrivers { get; } = new();
}

public static class Backends
{
    public static BackendInventory Probe()
    {
        var inv = new BackendInventory { SdlVersion = Sdl2dPath.SdlVersion() };
        for (int i = 0; i < SDL3.SDL.GetNumVideoDrivers(); i++) inv.VideoDrivers.Add(SDL3.SDL.GetVideoDriver(i) ?? "?");
        for (int i = 0; i < SDL3.SDL.GetNumRenderDrivers(); i++) inv.RenderDrivers.Add(SDL3.SDL.GetRenderDriver(i) ?? "?");
        for (int i = 0; i < SDL3.SDL.GetNumGPUDrivers(); i++) inv.GpuDrivers.Add(SDL3.SDL.GetGPUDriver(i) ?? "?");
        return inv;
    }
}

public static class RunSupport
{
    /// <summary>
    /// Owner-approved logical Commander logical screens used (read only) as input cells: dual panels,
    /// main menu, nested menu with shadow, modal dialog, jobs window, modifier status line, viewer,
    /// panel selection — across 80x25 and 140x40.
    /// </summary>
    public static readonly string[] CommanderSelection =
    [
        "commander-input-panel-selection--desktop--80x25",
        "commander-input-menu-mouse--managers-open--80x25",
        "commander-input-menu-mouse--window-open--140x40",
        "commander-input-dialog-mouse--typed--80x25",
        "commander-input-jobs-indicator--jobs--140x40",
        "commander-input-modifier-layers--shift--80x25",
        "commander-menu-status-availability--viewer--140x40",
        "commander-input-panel-selection--right-dragged--140x40",
    ];

    public static string? FindWorkbenchRoot() => TSharpWorkbench.Support.WorkbenchPaths.FindRoot();

    public static string WorkbenchOutputRoot => TSharpWorkbench.Support.WorkbenchPaths.OutputRoot;
}
