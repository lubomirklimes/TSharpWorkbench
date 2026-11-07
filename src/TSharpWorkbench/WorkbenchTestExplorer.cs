using TSharpVision.Constants;
using TSharpWorkbench.Storage;
using TSharpWorkbench.Testing;

internal static class WorkbenchTestExplorer
{
    internal const ushort Command = 0x7bb5, Context = 0x7bbc;
    private const ushort Add = 0x7bb6, Edit = 0x7bb7, Delete = 0x7bb8, Filter = 0x7bba, Scenario = 0x7bbb, Run = 0x7b95, MarkAllTests = 0x7bc0, ClearTestMarks = 0x7bc1;
    private sealed class ExplorerDialog(TRect bounds) : TWindow(bounds, "Test Explorer", 7)
    {
        internal Action<ushort>? Dispatch;
        internal Action? Closed;
        internal Action? Layout;
        internal Action? Refresh;

        public override void SizeLimits(ref TPoint min, ref TPoint max)
        {
            base.SizeLimits(ref min, ref max);
            min = new(Math.Min(72, max.x), Math.Min(17, max.y));
        }

        public override void ChangeBounds(TRect bounds)
        {
            base.ChangeBounds(bounds);
            Layout?.Invoke();
        }

        public override void HandleEvent(ref TEvent ev)
        {
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbEsc)
            { ClearEvent(ref ev); Close(); return; }
            if (ev.What == Events.evKeyDown && ev.keyDown.keyCode == Keys.kbF5)
            { ClearEvent(ref ev); Dispatch?.Invoke(Run); return; }
            base.HandleEvent(ref ev);
            if (ev.What == Events.evCommand && ev.message.command is Add or Edit or Delete or Filter or Scenario or Run or MarkAllTests or ClearTestMarks or Views.cmCancel)
            {
                ushort action = ev.message.command;
                ClearEvent(ref ev);
                if (action == Views.cmCancel) Close(); else Dispatch?.Invoke(action);
            }
        }
        public override void ShutDown() { base.ShutDown(); Closed?.Invoke(); }
    }

    internal static void Show(TDeskTop desktop, WorkbenchDialog project)
    {
        if (project.TestExplorer is ExplorerDialog existing) { existing.Refresh?.Invoke(); existing.Select(); if (!ReferenceEquals(desktop.current, existing)) desktop.SetCurrent(existing, TGroup.selectMode.normalSelect); return; }
        var session = project.Native!.Session;
        var marks = project.MarkedTests;
        string search = "", group = "All", organization = "All";
        {
            var scenarios = session.Scenarios.ToDictionary(s => s.Id);
            string Group(TestDefinition test) => scenarios.GetValueOrDefault(test.ScenarioId)?.Backend switch
            { "proxy-logical" => "Black-Box", "diagnostic-logical" => "White-Box", _ => "Unavailable" };
            string Organization(TestDefinition t) => project.ScenarioGroup(t.ScenarioId);
            var tests = session.Tests.Where(t => (group == "All" || Group(t) == group) && (organization == "All" || Organization(t) == organization) &&
                (t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
            int w = desktop.size.x, h = desktop.size.y;
            int listBottom = h - 6;
            var dialog = new ExplorerDialog(new(0, 0, w, h));
            dialog.flags = (byte)(Views.wfMove | Views.wfGrow | Views.wfClose | Views.wfZoom);
            int restoreWidth = Math.Clamp(w - 4, Math.Min(72, w), 108);
            int restoreHeight = Math.Clamp(h - 2, Math.Min(17, h), 22);
            int restoreX = (w - restoreWidth) / 2, restoreY = (h - restoreHeight) / 2;
            dialog.zoomRect = new(restoreX, restoreY, restoreX + restoreWidth, restoreY + restoreHeight);
            dialog.helpCtx = Context;
            var query = new TInputLine(new(12, 2, w - 20, 3), 100); query.SetData(search); dialog.Insert(query);
            dialog.Insert(new WindowLabel(new(3, 2, 11, 3), "~Q~uery", query));
            var organizations = new TComboBox(new(12, 3, w / 2, 4), new[] { "All" }.Concat(session.Tests.Select(Organization).Distinct().Order()), false, 100) { Text = organization };
            dialog.Insert(organizations); dialog.Insert(new WindowLabel(new(3, 3, 11, 4), "Gro~u~p", organizations));
            var modes = new TComboBox(new(w / 2 + 10, 3, w - 21, 4), ["All", "Black-Box", "White-Box", "Unavailable"], false, 20) { Text = group };
            dialog.Insert(modes);
            var modeLabel = new WindowLabel(new(w / 2 + 2, 3, w / 2 + 10, 4), "M~o~de", modes);
            dialog.Insert(modeLabel);
            var header = new ScenarioText(new(4, 5, w - 4, 6)); dialog.Insert(header);
            var scroll = new TScrollBar(new(w - 4, 6, w - 3, listBottom)); dialog.Insert(scroll);
            var list = new WorkbenchChoiceList<TestDefinition>(new(3, 6, w - 4, listBottom), scroll, tests, marks, t => t.Id,
                t => "     " + Columns(t.Title, Group(t), t.EnvironmentIds.Count.ToString(), project.TestScenarioStatus(t.ScenarioId)),
                activate: () => dialog.Dispatch?.Invoke(Edit));
            string Columns(string title, string mode, string environments, string status) =>
                WorkbenchText.Column(title, Math.Max(12, (dialog.size.x - 51) * 3 / 4)) + " " +
                WorkbenchText.Column(mode, 11) + " " + WorkbenchText.Column(environments, 12) + " " + status;
            dialog.Insert(list); list.SetRange(tests.Length);
            var actions = new Dictionary<ushort, TButton>();
            var buttonPositions = new List<(TButton Button, int Left, int Width)>();
            void Button(int x, int width, string caption, ushort action)
            {
                var button = new TButton(new(x, h - 3, x + width, h - 1), caption, action, ButtonConstants.bfNormal);
                actions[action] = button;
                buttonPositions.Add((button, x, width));
                dialog.Insert(button);
            }
            void Layout()
            {
                int width = dialog.size.x, height = dialog.size.y, middle = width / 2;
                int bottom = height - 6;
                query.Locate(new(12, 2, width - 21, 3));
                organizations.Locate(new(12, 3, middle, 4));
                modeLabel.Locate(new(middle + 2, 3, middle + 10, 4));
                modes.Locate(new(middle + 10, 3, width - 21, 4));
                header.Locate(new(4, 5, width - 4, 6));
                header.Set("Selected " + Columns("Test", "Mode", "Environments", "Status"));
                scroll.Locate(new(width - 4, 6, width - 3, bottom));
                list.Locate(new(3, 6, width - 4, bottom));
                foreach (var (button, left, buttonWidth) in buttonPositions)
                {
                    int x = left, y = height - 3;
                    if (button.Command == Filter) { x = width - 20; y = 2; }
                    else if (button.Command == Views.cmCancel) x = width - 13;
                    else if (button.Command is MarkAllTests or ClearTestMarks or Run) y -= 2;
                    button.Locate(new(x, y, x + buttonWidth, y + 2));
                }
            }
            void EnableSelectionActions()
            {
                foreach (ushort command in new[] { Edit, Delete, Scenario })
                {
                    if (tests.Length == 0) TView.DisableCommand(command); else TView.EnableCommand(command);
                    actions[command].SetState(Views.sfDisabled, tests.Length == 0);
                }
                bool hasTests = session.Tests.Count > 0;
                foreach (ushort command in new[] { MarkAllTests, ClearTestMarks })
                {
                    if (hasTests) TView.EnableCommand(command); else TView.DisableCommand(command);
                    actions[command].SetState(Views.sfDisabled, !hasTests);
                }
                if (dialog.current is TButton button && button.GetState(Views.sfDisabled)) list.Select();
            }
            Button(w - 20, 16, "~A~pply filter", Filter);
            Button(3, 9, "~N~ew", Add);
            Button(13, 9, "~E~dit", Edit);
            Button(23, 12, "~D~elete", Delete);
            Button(37, 13, "Scenar~i~o", Scenario);
            Button(33, 13, "Con~f~igure", Run);
            Button(3, 12, "~M~ark All", MarkAllTests);
            Button(16, 15, "C~l~ear Marks", ClearTestMarks);
            Button(w - 13, 10, "~C~lose", Views.cmCancel);
            dialog.Layout = Layout;
            Layout();
            EnableSelectionActions();
            list.Select();
            project.TestExplorer = dialog;
            dialog.Closed = () => project.TestExplorer = null;
            dialog.Dispatch = action =>
            {
                session = project.Native!.Session;
                if (action == Run) { project.ConfigureMatrix(); return; }
                if (action is MarkAllTests or ClearTestMarks)
                {
                    if (session.Tests.Count == 0) return;
                    var command = new TEvent { What = Events.evCommand };
                    command.message.command = action == MarkAllTests ? WorkbenchDialog.MarkAll : WorkbenchDialog.ClearSelection;
                    project.HandleEvent(ref command);
                    list.DrawView();
                    return;
                }
                if (action == Filter) { search = query.Data.Trim(); group = modes.Text; organization = organizations.Text; }
                var selected = tests.ElementAtOrDefault(list.focused);
                try
                {
                    if (action == Add || action == Edit && selected is not null)
                        EditTest(desktop, session, action == Edit ? selected : null, metadata: project.TestScenarioMetadata);
                    else if (action == Delete && selected is not null && WorkbenchDialog.Confirm(desktop, "Remove Test", "Remove test " + selected.Title + "? Its scenario remains available.", "~R~emove") == Views.cmYes)
                        session.RemoveTest(selected.Id);
                    else if (action == Scenario && selected is not null)
                    {
                        if (scenarios.GetValueOrDefault(selected.ScenarioId)?.ArtifactPath is { } path)
                        { project.OpenScenarioEditor(session.ResolveArtifact(path)); return; }
                        WorkbenchDialog.ShowMessage(desktop, "Scenario Editor", "This compiled scenario is read-only.");
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
                { WorkbenchDialog.ShowMessage(desktop, "Test Not Changed", ex.Message); }
                Refresh();
                if (action is Add or Edit or Delete) project.RefreshNativeTitle();
            };
            dialog.Refresh = Refresh;
            void Refresh()
            {
                session = project.Native!.Session;
                scenarios = session.Scenarios.ToDictionary(s => s.Id);
                string[] groups = ["All", .. session.Tests.Select(Organization).Distinct().Order()];
                if (!organizations.Items.SequenceEqual(groups)) organizations.SetItems(groups);
                string? selectedId = tests.ElementAtOrDefault(list.focused)?.Id;
                int top = list.topItem;
                tests = session.Tests.Where(t => (group == "All" || Group(t) == group) && (organization == "All" || Organization(t) == organization) &&
                    (t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
                int index = Array.FindIndex(tests, t => t.Id == selectedId);
                list.Reload(tests);
                if (index >= 0)
                {
                    list.FocusItemNum(index);
                    list.topItem = Math.Clamp(top, Math.Max(0, index - list.size.y + 1), index);
                }
                list.DrawView();
                EnableSelectionActions();
            }
            desktop.Insert(dialog);
            dialog.Select();
            if (!ReferenceEquals(desktop.current, dialog)) desktop.SetCurrent(dialog, TGroup.selectMode.normalSelect);
            dialog.DrawView();
        }
    }

    private sealed class TestDialog(TRect bounds, string caption) : TDialog(bounds, caption)
    {
        internal Func<bool>? SaveDraft;
        public override bool Valid(ushort command) =>
            command == Views.cmOK ? SaveDraft?.Invoke() == true : base.Valid(command);
    }

    private sealed record ScenarioOption(string Id, string Label, bool Exists);

    private sealed class EnvironmentLabel(TRect bounds, TView list, HashSet<string> selected) : TLabel(bounds, $"~E~nvironments ({selected.Count} selected)", list)
    {
        internal void Update()
        {
            Text = $"~E~nvironments ({selected.Count} selected)";
            DrawView();
        }
    }

    internal static void EditTest(TDeskTop desktop, ProjectSession session, TestDefinition? test, string? scenarioId = null,
        Func<string, ScenarioDescriptor?>? metadata = null)
    {
        var scenarios = session.Scenarios;
        var environments = session.Environments.ToArray();
        if (test is null && (scenarios.Count == 0 || environments.Length == 0))
        { WorkbenchDialog.ShowMessage(desktop, "Test Needs Assignments", "Create a Test from Scenario Manager, or import a scenario and add at least one environment first."); return; }

        string assignedScenario = test?.ScenarioId ?? scenarioId ?? scenarios.FirstOrDefault()?.Id ?? "";
        var options = scenarios.Select(reference =>
        {
            ScenarioDescriptor? descriptor = reference.ArtifactPath is { } path
                ? ScenarioFiles.Inspect(session.ResolveArtifact(path)).Document?.Scenario : metadata?.Invoke(reference.Id);
            string? name = descriptor?.Name;
            string label = !string.IsNullOrWhiteSpace(name) && name != reference.Id
                ? $"{name} [{reference.Id}]"
                : $"{reference.Id} — {(reference.Backend == "proxy-logical" ? "Black-Box scenario" : "White-Box scenario")}";
            if (descriptor is null) label += reference.ArtifactPath is null ? " (name unavailable)" : " (unavailable)";
            return new ScenarioOption(reference.Id, label, true);
        }).ToList();
        if (assignedScenario.Length > 0 && options.All(option => option.Id != assignedScenario))
            options.Add(new(assignedScenario, $"{assignedScenario} — Missing scenario", false));

        int w = Math.Min(90, desktop.size.x - 2), h = Math.Min(29, desktop.size.y);
        using var dialog = new TestDialog(new(0, 0, w, h), test is null ? "New Test" : "Edit Test");
        dialog.options |= Views.ofCentered;
        dialog.helpCtx = Context;
        var title = new TInputLine(new(20, 2, w - 4, 3), Math.Max(1024, test?.Title.Length ?? 0));
        title.SetData(test?.Title ?? "");
        dialog.Insert(title); dialog.Insert(new TLabel(new(3, 2, 19, 3), "~T~itle", title));

        int memoRows = Math.Clamp(h - 22, 2, 4);
        TMemo Memo(string label, string value, int top)
        {
            uint capacity = (uint)Math.Max(65536L, (long)value.Length + 16384);
            var vertical = new TScrollBar(new(w - 5, top, w - 4, top + memoRows)); dialog.Insert(vertical);
            var horizontal = new TScrollBar(new(20, top + memoRows, w - 5, top + memoRows + 1)); dialog.Insert(horizontal);
            var memo = new TMemo(new(20, top, w - 5, top + memoRows), horizontal, vertical, null, capacity)
                { growMode = 0, canUndo = true };
            var data = new TMemoData((uint)value.Length) { length = (uint)value.Length, buffer = value.ToCharArray() };
            memo.SetData(data);
            dialog.Insert(memo);
            dialog.Insert(new TLabel(new(3, top, 19, top + 1), label, memo));
            return memo;
        }
        var description = Memo("~D~escription", test?.Description ?? "", 4);
        int preconditionsTop = 4 + memoRows + 2;
        var preconditions = Memo("~P~reconditions", test?.Preconditions ?? "", preconditionsTop);
        int scenarioTop = preconditionsTop + memoRows + 2;
        var scenario = new TComboBox(new(20, scenarioTop, w - 4, scenarioTop + 1), options.Select(option => option.Label), false, 1024)
            { SelectedIndex = options.FindIndex(option => option.Id == assignedScenario) };
        dialog.Insert(scenario); dialog.Insert(new TLabel(new(3, scenarioTop, 19, scenarioTop + 1), "Scenari~o~", scenario));
        var selected = new HashSet<string>(test?.EnvironmentIds ?? [], StringComparer.Ordinal);
        int environmentTop = scenarioTop + 3;
        var scroll = new TScrollBar(new(w - 4, environmentTop, w - 3, h - 4)); dialog.Insert(scroll);
        EnvironmentLabel? environmentLabel = null;
        var list = new WorkbenchChoiceList<EnvironmentDefinition>(new(3, environmentTop, w - 4, h - 4), scroll, environments, selected,
            environment => environment.Id,
            environment => $"{environment.Name} [{environment.Id}]  {environment.OperatingSystem} {environment.Driver} {environment.Width}x{environment.Height}"
                + (environment.Enabled ? "" : " (disabled)"),
            () => environmentLabel?.Update());
        dialog.Insert(list); list.SetRange(environments.Length);
        environmentLabel = new EnvironmentLabel(new(3, environmentTop - 1, w - 4, environmentTop), list, selected);
        dialog.Insert(environmentLabel);
        dialog.Insert(new TButton(new(w - 29, h - 3, w - 17, h - 1), "~S~ave", Views.cmOK, ButtonConstants.bfDefault));
        dialog.Insert(new TButton(new(w - 15, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfNormal));
        title.Select();

        dialog.SaveDraft = () =>
        {
            ScenarioOption? option = options.ElementAtOrDefault(scenario.SelectedIndex);
            string? error = null;
            TView invalid = title;
            if (title.Data.Trim().Length == 0) error = "Enter a title.";
            else if (option is null || !option.Exists || !session.Scenarios.Any(reference => reference.Id == option.Id))
            { error = "Choose an available scenario. The original assignment has not been replaced."; invalid = scenario; }
            else if (selected.Count == 0 || selected.Any(id => !session.Environments.Any(environment => environment.Id == id)))
            { error = "Select at least one existing environment. Remove any unavailable assignments."; invalid = list; }
            if (error is not null)
            {
                WorkbenchDialog.ShowMessage(desktop, "Test Needs Assignments", error, Context);
                invalid.Select();
                return false;
            }
            try
            {
                session.PutTest(new(test?.Id ?? Guid.NewGuid().ToString("N"), title.Data.Trim(),
                    Text(description), Text(preconditions), option!.Id, selected.ToArray()));
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                WorkbenchDialog.ShowMessage(desktop, "Test Not Changed", ex.Message, Context);
                return false;
            }
        };
        desktop.ExecView(dialog);

        static string Text(TMemo memo)
        {
            var data = new TMemoData(memo.bufLen);
            memo.GetData(data);
            return new string(data.buffer, 0, (int)data.length);
        }
    }

}

internal sealed partial class WorkbenchDialog
{
    internal ScenarioDescriptor? TestScenarioMetadata(string id) =>
        catalog.FirstOrDefault(scenario => scenario.Id == id) ?? CompiledForTarget?.FirstOrDefault(scenario => scenario.Id == id);

    internal TWindow? TestExplorer { get; set; }
    internal TWindow? EnvironmentManager { get; set; }
    internal string ScenarioGroup(string id) => catalog.FirstOrDefault(s => s.Id == id)?.Group ??
        AttachedScenarios.FirstOrDefault(s => s.Info.Id == id)?.Info.Document?.Scenario.Group ?? "Ungrouped";
    internal string TestScenarioStatus(string id)
    {
        var reference = Native?.Session.Scenarios.FirstOrDefault(s => s.Id == id);
        if (reference is null) return "Missing scenario";
        if (reference.ArtifactPath is null) return catalog.Any(s => s.Id == id) ? "Compiled" : "Compiled scenario unavailable";
        var path = Native!.Session.ResolveArtifact(reference.ArtifactPath);
        return AttachedScenarios.FirstOrDefault(s => WorkbenchPreferences.SamePath(s.Path, path))?.Status ?? "Not checked";
    }
}
