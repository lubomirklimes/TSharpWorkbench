namespace TSharpWorkbench.Testing;

/// <summary>Opt-in evidence from the proxy-owned backend, separate from the logical checkpoint.</summary>
public sealed record ProxyNativeCapture(string Kind, string Format, string Data, int PixelWidth, int PixelHeight,
    string Sha256, Dictionary<string, string> Details);

public static class ProxyCaptureKinds
{
    public const string None = "none", Renderer = "renderer-pixels", Gpu = "gpu-completed", Console = "native-console-cells";
    public static readonly string[] All = [None, Renderer, Gpu, Console];
    public static string[] ForBackend(string backend) => backend switch
    {
        "SDLDriver" => [Renderer], "SDLGpuDriver" => [Renderer, Gpu],
        "Win32ConsoleDriver" when OperatingSystem.IsWindows() => [Console], _ => []
    };
    internal const int MaximumPayloadBytes = 1024 * 1024;
}
