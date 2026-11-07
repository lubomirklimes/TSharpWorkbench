using System.Reflection;
using TSharpVision.Drivers;

namespace TSharpWorkbench.Testing;

// Diagnostic implementation contract, deliberately internal. NativePixels already depends on Testing;
// the fixed provider is staged by the diagnostic application, never selected from the wire or a path.
internal interface INativeExecution : IDisposable
{
    IDriver Driver { get; }
    string Routing { get; }
    void Inject(string key);
    void InjectMouse(TEvent ev);
    NativeCheckpoint Capture();
}

internal sealed record NativeCheckpoint(string Png, string PixelsHash, string Layer,
    int PixelWidth, int PixelHeight, Dictionary<string, string> Provenance);

internal static class NativeExecution
{
    internal const string GpuCleanupMarker = "TW6B2B_GPU_CLEANUP_VERIFIED: worker joined; renderer/device/window released; SDL/TTF=0";
    internal const string Variable = "TSHARPVISION_TESTING_NATIVE";
    internal static readonly string[] GpuModes = ["SDLGpuDriver", "SDLGpuDriver-D3D12Fallback", "SDLGpuDriver-Vulkan"];
    internal static bool IsGpu(string mode) => GpuModes.Contains(mode);
    internal static bool IsMode(string mode) => mode is "SDLDriver" or "Win32ConsoleDriver" || IsGpu(mode);
    internal static string ActualDriver(string mode) => IsGpu(mode) ? "SDLGpuDriver" : mode;
    internal static string Backend(string mode) => mode == "SDLGpuDriver-Vulkan" ? "vulkan" : "direct3d12";
    internal static string GpuPath(string mode) => mode == "SDLGpuDriver-D3D12Fallback" ? "cpu-composite" : "shader";
    internal static bool Supports(ScenarioDescriptor scenario) => Supports(scenario, "SDLDriver");
    internal static bool Supports(ScenarioDescriptor scenario, string mode) => Refusal(scenario, mode) is null;

    /// <summary>
    /// The registration whose audited native workflow a runnable scenario executes. A compiled scenario (or, inside the
    /// target, the launched registration) is its own ID; an external scenario is the fixture of its validated binding (fixture
    /// ID plus the contract fingerprint recorded at merge and re-verified at launch), and only for the diagnostic backend.
    /// Ordinary Proxy scenarios and unbound documents have none, whatever their IDs or "fixture" text look like.
    /// </summary>
    internal static string? NativeIdentity(ScenarioDescriptor scenario) => scenario.External switch
    {
        null => scenario.Id,
        { FixtureId.Length: > 0, FixtureHash.Length: > 0 } binding when scenario.Backend == "diagnostic-logical" => binding.FixtureId,
        _ => null,
    };

    /// <summary>Why a scenario cannot run with a native driver, or null when it can. The audited set is unchanged: the identity
    /// (own or bound fixture) must be one of the audited workflows and every step of the executing scenario must be an audited
    /// key, a left click (not on the console host) or a checkpoint. Actions, text, resize and other input stay blocked.</summary>
    internal static string? Refusal(ScenarioDescriptor scenario, string mode)
    {
        if (!IsMode(mode)) return $"'{mode}' is not a supported native driver";
        // The one genuine platform prerequisite of a native driver (the console host is Windows-only).
        if (mode == "Win32ConsoleDriver" && !OperatingSystem.IsWindows()) return "Win32ConsoleDriver requires Windows";
        if (NativeIdentity(scenario) is not { } identity)
            return scenario.Backend == "proxy-logical" ? "ordinary Proxy scenario: the diagnostic native drivers do not apply (use the Proxy backend)"
                : "external scenario without a validated fixture binding";
        if (!(Audited.Contains(identity) || mode == "Win32ConsoleDriver" && identity == ConsoleOnly))
            return (identity == scenario.Id ? $"'{identity}'" : $"bound fixture '{identity}'") + $" is not an audited native workflow for {mode}";
        if (mode == "Win32ConsoleDriver" && scenario.Steps.Any(s => s.Kind == InputSteps.Mouse)) return "Win32ConsoleDriver has no audited mouse input";
        if (scenario.Steps.FirstOrDefault(s => !(s.Kind == ScenarioStep.Checkpoint || s.Kind == ScenarioStep.Key && AuditedKeys.Contains(s.Name)
            || s.Kind == InputSteps.Mouse && s.Name.StartsWith("click-left@", StringComparison.Ordinal))) is { } step)
            return $"step {step.Kind} '{(step.Kind == InputSteps.Text ? "[redacted]" : step.Name)}' is not audited for native execution";
        return null;
    }
    private static readonly string[] Audited = ["commander/menu/f10-open-close", "commander/keyboard/copy-rename-here", "commander/input/menu-mouse", "commander/menu-status/nested-submenu"];
    private const string ConsoleOnly = "commander/menu-status/menu-opens-jobs";
    private static readonly string[] AuditedKeys = ["kbF10", "kbRight", "kbLeft", "kbDown", "kbUp", "kbEnter", "kbEsc", "kbTab", "kbShiftF5", "kbShiftF6", "kbAltW"];
    internal static bool IsQuitConfirmation(TestTargetContext context)
    {
        if (context.TopView is not TDialog { current: TButton { Command: Constants.Views.cmYes } } dialog || dialog.last is null) return false;
        TView first = dialog.last.Next!;
        for (TView? view = first; view is not null; view = view.Next)
        {
            if (view is TStaticText text) { text.GetText(out string value); if (value == "Quit T# Commander?") return true; }
            if (view.Next == first) break;
        }
        return false;
    }
    internal static INativeExecution Create(string mode, int width, int height)
    {
        if (!IsMode(mode)) throw new InvalidOperationException("Unsupported native driver; no fallback performed.");
        string provider = mode != "Win32ConsoleDriver" ? "TSharpWorkbench.Diagnostics.NativePixels" : "TSharpWorkbench.Diagnostics.NativeHosts";
        string assemblyIdentity = mode != "Win32ConsoleDriver" ? "TSharpWorkbench.Diagnostics.NativePixels" : "TSharpWorkbench.Diagnostics.NativeHosts";
        var assembly = NativeProviderDeployment.Load(assemblyIdentity);
        var type = assembly.GetType(provider + (IsGpu(mode) ? ".GpuApplicationExecution" : ".ApplicationExecution"), throwOnError: true)!;
        return (INativeExecution)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, IsGpu(mode) ? [width, height, mode] : [width, height], null)!;
    }
}
