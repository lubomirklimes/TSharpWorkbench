using System.Diagnostics;
using System.Globalization;
using TSharpVision.Drivers.SDL;
using TSharpVision.Drivers.SDL.Gpu;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

internal static class BorrowedNativeCapture
{
    /// <summary>
    /// UI thread: performs only the owner-thread part (renderer readback inside one frame, or GPU
    /// submission of one pinned frame). GPU completion and PNG encoding finish off the UI thread.
    /// </summary>
    internal static Task<ProxyNativeCapture> Begin(TSharpVision.Drivers.IDriver backend, string kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        Task<SdlDiagnosticCapture> frame = backend switch
        {
            SDLDriver renderer when kind == ProxyCaptureKinds.Renderer => Task.FromResult(renderer.CaptureDiagnosticPixels()),
            SDLGpuDriver gpu when kind is ProxyCaptureKinds.Renderer or ProxyCaptureKinds.Gpu => gpu.BeginDiagnosticCapture(token),
            _ => throw new NotSupportedException("Requested native capture kind is unavailable; no fallback.")
        };
        return Complete(frame, kind, sw, token);
    }

    private static async Task<ProxyNativeCapture> Complete(Task<SdlDiagnosticCapture> pending, string kind,
        Stopwatch sw, CancellationToken token)
    {
        // Leave the UI thread before any waiting or encoding.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        SdlDiagnosticCapture frame = await pending.ConfigureAwait(false);
        if (kind == ProxyCaptureKinds.Gpu && frame.Layer != ProxyCaptureKinds.Gpu)
            throw new NotSupportedException("GPU shader path unavailable; CPU-composite output cannot satisfy gpu-completed.");
        token.ThrowIfCancellationRequested();
        // Encode the owned RGBA readback, including alpha; never invoke logical glyph rendering.
        int stride = checked(frame.Width * 4 + 1);
        byte[] raster = new byte[checked(stride * frame.Height)];
        for (int y = 0; y < frame.Height; y++)
            frame.Rgba.AsSpan(y * frame.Width * 4, frame.Width * 4).CopyTo(raster.AsSpan(y * stride + 1));
        byte[] png = LogicalPng.EncodeRaster(frame.Width, frame.Height, raster, channels: 4);
        if (png.Length > ProxyCaptureKinds.MaximumPayloadBytes)
            throw new NotSupportedException("Native PNG exceeds the bounded 1 MiB payload limit.");
        var details = new Dictionary<string, string>(frame.Details)
        {
            ["captureDurationMs"] = sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            ["payloadBytes"] = png.Length.ToString(), ["rgbaBytes"] = frame.Rgba.Length.ToString(),
            ["ownership"] = "borrowed current backend; proxy owns lifecycle",
            ["requestedKind"] = kind
        };
        return new(frame.Layer, "png", Convert.ToBase64String(png), frame.Width, frame.Height, ScreenSnapshot.Hash(png), details);
    }
}
