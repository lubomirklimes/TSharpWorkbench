using TSharpVision.Constants;
using TSharpWorkbench.Storage;

internal static class WorkbenchEnvironments
{
    internal const ushort Context = 0x7b9b;
    private static string LocalOs => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";

    internal static void Provision(ProjectSession session)
    {
        if (session.Environments.Count == 0)
            session.PutEnvironment(new("local-headless", "Local headless", "host", "NullDriver", 80, 25, "none"));
    }

    private const ushort Add = 0x7bb0, EditProfile = 0x7bb1, RemoveProfile = 0x7bb2;
    private sealed class ManagerDialog(TRect bounds) : TDialog(bounds, "Environment Manager")
    {
        internal Action<ushort>? Dispatch;
        public override void HandleEvent(ref TEvent ev)
        {
            base.HandleEvent(ref ev);
            if (ev.What == Events.evCommand && ev.message.command is Add or EditProfile or RemoveProfile)
            {
                ushort action = ev.message.command;
                ClearEvent(ref ev);
                Dispatch?.Invoke(action);
            }
        }
    }

    internal static string Availability(EnvironmentDefinition profile, string target)
    {
        if (!profile.Enabled) return "Disabled";
        string host = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        if (profile.OperatingSystem != "host" && profile.OperatingSystem != host) return "Different OS";
        if (profile.Driver == "Win32ConsoleDriver" && (host == "host" ? LocalOs : host) != "windows") return "Driver unavailable";
        if (!File.Exists(target)) return "Target needed";
        return profile.Driver == "NullDriver" ? "Headless request" : "Driver unverified";
    }

    internal static void Show(TDeskTop desktop, WorkbenchDialog project)
    {
        if (project.EnvironmentManager is not null) return;
        var session = project.Native!.Session;
        int w = Math.Min(100, desktop.size.x - 2);
        var profiles = session.Environments;
        using var dialog = new ManagerDialog(new(0, 0, w, 15));
        dialog.options |= Views.ofCentered;
        dialog.helpCtx = Context;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, 4),
            "Profiles describe requested settings. Foreign OS profiles cannot execute locally.\nName                      OS       Driver             Grid       Status"));
        var scroll = new TScrollBar(new(w - 4, 4, w - 3, 11)); dialog.Insert(scroll);
        var list = new TListBox(new(3, 4, w - 4, 11), 1, scroll); dialog.Insert(list);
        void Reload(string? id = null)
        {
            int top = list.topItem;
            session = project.Native!.Session;
            profiles = session.Environments;
            var lines = new TStringCollection();
            foreach (var profile in profiles)
                lines.Insert($"{WorkbenchText.Column(profile.Name, 25)} {WorkbenchText.Column(profile.OperatingSystem, 8)} {WorkbenchText.Column(profile.Driver, 18)} {profile.Width,4}x{profile.Height,-4} {Availability(profile, project.CaptureProject().Target)}");
            list.NewList(lines);
            int index = profiles.ToList().FindIndex(p => p.Id == id);
            if (index >= 0) list.FocusItemNum(index);
            list.topItem = Math.Clamp(top, 0, Math.Max(0, profiles.Count - 1));
        }
        Reload();
        dialog.Insert(new TButton(new(3, 12, 14, 14), "~N~ew", Add, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(16, 12, 27, 14), "~E~dit", EditProfile, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(29, 12, 42, 14), "~R~emove", RemoveProfile, ButtonConstants.bfNormal));
        dialog.Insert(new TButton(new(w - 15, 12, w - 3, 14), "~C~lose", Views.cmCancel, ButtonConstants.bfDefault));
        list.Select();
        dialog.Dispatch = action =>
        {
            session = project.Native!.Session;
            var selected = profiles.ElementAtOrDefault(list.focused);
            try
            {
                if (action == Add) Edit(desktop, session, new(Guid.NewGuid().ToString("N"), "", LocalOs, "NullDriver", 80, 25, "none"));
                else if (action == EditProfile && selected is not null) Edit(desktop, session, selected);
                else if (action == RemoveProfile && selected is not null &&
                    WorkbenchDialog.Confirm(desktop, "Remove Environment", "Remove " + selected.Name + "? Tests using it must be updated first.", "~R~emove") == Views.cmYes)
                    session.RemoveEnvironment(selected.Id);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
            { WorkbenchDialog.ShowMessage(desktop, "Environment Not Changed", ex.Message); }
            Reload(selected?.Id);
            project.RefreshNativeTitle();
        };
        project.EnvironmentManager = dialog;
        try { desktop.ExecView(dialog); }
        finally { project.EnvironmentManager = null; }
    }

    private sealed class ValidationLine(TRect bounds) : TStaticText(bounds, "")
    {
        internal void Show(string message) { Text = WorkbenchText.Fit(message, size.x); DrawView(); }
    }

    private static void Edit(TDeskTop desktop, ProjectSession session, EnvironmentDefinition profile)
    {
        const int w = 70, h = 17;
        using var dialog = new TDialog(new(0, 0, w, h), "Environment Properties");
        dialog.options |= Views.ofCentered;
        dialog.helpCtx = Context;
        TInputLine Field(string label, string value, int y)
        {
            var input = new TInputLine(new(23, y, w - 4, y + 1), 80);
            input.SetData(value); dialog.Insert(input);
            dialog.Insert(new TLabel(new(3, y, 22, y + 1), label, input));
            return input;
        }
        var name = Field("Na~m~e", profile.Name, 2);
        TComboBox Combo(string label, string[] choices, string value, int y, bool editable = false)
        {
            var combo = new TComboBox(new(23, y, w - 4, y + 1), choices, editable, 20) { Text = value };
            dialog.Insert(combo);
            dialog.Insert(new TLabel(new(3, y, 22, y + 1), label, combo));
            return combo;
        }
        string portable = $"Current host ({LocalOs})";
        string[] systems = [portable, "Windows", "Linux", "macOS"];
        var os = Combo("~O~perating System", systems, profile.OperatingSystem == "host" ? portable : systems.FirstOrDefault(x => x.Equals(profile.OperatingSystem, StringComparison.OrdinalIgnoreCase)) ?? "", 4);
        var drivers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Headless (NullDriver)"] = "NullDriver",
            ["SDL software (SDLDriver)"] = "SDLDriver",
            ["SDL GPU (SDLGpuDriver)"] = "SDLGpuDriver",
            ["Windows Console (Win32ConsoleDriver)"] = "Win32ConsoleDriver"
        };
        var driver = Combo("~D~river", drivers.Keys.ToArray(), drivers.FirstOrDefault(d => d.Value == profile.Driver).Key ?? "", 6);
        void FilterDrivers()
        {
            string previous = driver.Text;
            bool windows = os.Text == "Windows" || os.Text == portable && LocalOs == "windows";
            var compatible = drivers.Where(d => windows || d.Value != "Win32ConsoleDriver").Select(d => d.Key).ToArray();
            driver.SetItems(compatible);
            driver.Text = compatible.Contains(previous) ? previous : compatible[0];
        }
        os.SelectionChanged += (_, _) => FilterDrivers();
        FilterDrivers();
        string DriverId() => drivers.GetValueOrDefault(driver.Text, "");
        var grid = Combo("~R~esolution", ["80x25", "120x36", "140x40", "160x50"], $"{profile.Width}x{profile.Height}", 8, true);
        string CaptureLabel(string value) => value == "none" ? "None" : value;
        string[] CaptureValues() => ["none", .. TSharpWorkbench.Testing.ProxyCaptureKinds.ForBackend(DriverId()).Where(x => x != "none")];
        var capture = Combo("Ca~p~ture", CaptureValues().Select(CaptureLabel).ToArray(),
            CaptureLabel(CaptureValues().Contains(profile.Capture) ? profile.Capture : "none"), 10);
        driver.SelectionChanged += (_, _) =>
        {
            string previous = capture.Text;
            string[] choices = CaptureValues().Select(CaptureLabel).ToArray();
            capture.SetItems(choices);
            capture.Text = choices.Contains(previous) ? previous : "None";
        };
        var enabled = new TCheckBoxes(new(23, 12, w - 4, 13), new TSItem("Ena~b~led", null!))
        { value = profile.Enabled ? 1u : 0u };
        dialog.Insert(enabled);
        dialog.Insert(new TButton(new(w - 29, 14, w - 17, 16), "~S~ave", Views.cmOK, ButtonConstants.bfDefault));
        dialog.Insert(new TButton(new(w - 15, 14, w - 3, 16), "~C~ancel", Views.cmCancel, ButtonConstants.bfNormal));
        var validation = new ValidationLine(new(3, 13, w - 3, 14));
        dialog.Insert(validation);
        name.Select();
        while (desktop.ExecView(dialog) == Views.cmOK)
        {
            string host = os.Text == portable ? "host" : os.Text.Trim().ToLowerInvariant();
            string captureKind = capture.Text == "None" ? "none" : capture.Text;
            if (string.IsNullOrWhiteSpace(name.Data) || host is not ("host" or "windows" or "linux" or "macos") ||
                DriverId().Length == 0 || !CaptureValues().Contains(captureKind) ||
                (DriverId() == "Win32ConsoleDriver" && (host == "host" ? LocalOs : host) != "windows"))
            { validation.Show("Enter a name and compatible OS, driver and capture."); continue; }
            if (!TSharpWorkbench.Testing.ScenarioSelection.TryParseSize(grid.Text, out int x, out int y) ||
                x is < 1 or > 1000 || y is < 1 or > 1000)
            { validation.Show("Enter a valid WIDTHxHEIGHT resolution."); continue; }
            if (session.Environments.Any(e => e.Id != profile.Id && e.Name.Equals(name.Data.Trim(), StringComparison.OrdinalIgnoreCase)))
            { validation.Show("Choose a unique environment name."); continue; }
            session.PutEnvironment(profile with
            {
                Name = name.Data.Trim(),
                OperatingSystem = host,
                Driver = DriverId(), Width = x, Height = y, Capture = captureKind, Enabled = enabled.value == 1
            });
            return;
        }
    }
}


