using System.Diagnostics;
using TSharpVision;
using TSharpWorkbench.Testing;

/// <summary>
/// Quick launch of the project's target application without recording or a scenario (Runs > Launch Application).
/// On Windows the target gets its own console in a kill-on-close job, so Stop and Workbench exit end the whole tree.
/// </summary>
internal static class WorkbenchTargetLauncher
{
    private static OwnedConsoleProcess? owned;
    private static Process? child;
    private static string? root;

    internal static bool Running
    {
        get
        {
            var process = owned?.Process ?? child;
            try { return process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>Recording state shown on the menu bar: off, recording or paused.</summary>
    internal static RecordingState? Recording => WorkbenchRecordingWindow.Active?.Session is { Live: true } session ? session.State : null;

    internal static void Start(WorkbenchProject project)
    {
        if (Running) throw new InvalidOperationException("The application is already running.");
        Cleanup();
        if (project.Target is not { Length: > 0 } target) throw new ArgumentException("The project has no target executable; set it in Settings > Project Properties first.");
        target = Path.GetFullPath(target);
        if (!File.Exists(target)) throw new ArgumentException(Path.GetFileName(target) + " does not exist.");
        TargetLaunch.Validate(project.TargetArguments, project.TargetEnvironment);
        root = Path.Combine(Path.GetTempPath(), TempRoots.Prefix + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "state"), fixture = Path.Combine(root, "fixture"), directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(state); Directory.CreateDirectory(fixture);
        TempRoots.WriteMarker(root);
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = directory };
        foreach (string argument in project.TargetArguments) start.ArgumentList.Add(TargetLaunch.Expand(argument, state, fixture, directory));
        foreach (var pair in project.TargetEnvironment) start.Environment[pair.Key] = TargetLaunch.Expand(pair.Value, state, fixture, directory);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("TSHARPVISION_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        if (OperatingSystem.IsWindows()) owned = OwnedConsoleProcess.Start(start);
        else child = Process.Start(start) ?? throw new IOException("The application did not start.");
    }

    internal static void Stop()
    {
        try
        {
            if (owned is not null) { owned.Terminate(); owned.Process.WaitForExit(5000); }
            else if (child is { HasExited: false }) { child.Kill(true); child.WaitForExit(5000); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Cleanup();
    }

    private static void Cleanup()
    {
        owned?.Process?.Dispose(); owned?.Dispose(); owned = null;
        child?.Dispose(); child = null;
        if (root is not null) { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } root = null; }
    }
}

/// <summary>Menu bar with the target application and scenario recording state at the right edge.</summary>
internal sealed class WorkbenchMenuBar(TRect bounds, TSubMenu menu) : TMenuBar(bounds, menu)
{
    private string drawn = "";

    internal static string AppText(bool running) => running ? "App: running" : "App: stopped";
    internal static string RecordText(RecordingState? state) => state switch
    {
        RecordingState.Recording => "Rec: on",
        RecordingState.Paused => "Rec: paused",
        RecordingState.Starting => "Rec: starting",
        RecordingState.Stopping => "Rec: stopping",
        _ => "Rec: off",
    };

    internal Func<bool> AppRunning { get; set; } = () => WorkbenchTargetLauncher.Running;

    private (string App, string Rec) Current()
    {
        var recording = WorkbenchTargetLauncher.Recording;
        return (AppText(AppRunning() || recording is not null), RecordText(recording));
    }

    internal void Refresh()
    {
        var (app, rec) = Current();
        if (app + rec != drawn) DrawView();
    }

    public override void Draw()
    {
        base.Draw();
        var (app, rec) = Current();
        drawn = app + rec;
        int used = 0;
        for (var item = Menu?.Items; item is not null; item = item.Next)
            if (item.Name is not null) used = Math.Max(used, GetItemRect(item).b.x);
        string text = app + " | " + rec + " ";
        int x = size.x - text.Length;
        if (x <= used + 1) return;
        bool appOn = !app.EndsWith("stopped", StringComparison.Ordinal), recOn = !rec.EndsWith("off", StringComparison.Ordinal);
        const byte normal = 0x70, active = 0x74;
        WriteStr(x, 0, app, appOn ? active : normal);
        WriteStr(x + app.Length, 0, " | ", normal);
        WriteStr(x + app.Length + 3, 0, rec + " ", recOn ? active : normal);
    }
}
