using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Recorder UX finalization (coordinator): the six approved owner decisions. 1 Smart capture default ON (explicit OFF
// stays OFF); 2 safe high-confidence preselection; 3 Review opens automatically after a user Stop; 4 confirmation for
// unaccepted proposals at Append/Insert/Replace; 5 stale Proxy: capability probe, Fix Issues / inputs only / Cancel, and the
// runtime handshake guard; 6 Windows Terminal limitation text. Logical NullDriver screenshots when TSWB_LAYOUT_EVIDENCE is set (NOT native).
[Collection("NonParallel")]
public sealed class RecorderUxTests
{
    private static string? Evidence => Environment.GetEnvironmentVariable("TSWB_LAYOUT_EVIDENCE") is { Length: > 0 } e ? e : null;

    // ── decision 1: default ON, explicit OFF stays OFF, environment and test seam precedence ────────────────────────

    [Theory]
    [InlineData(null, null, null, true)]      // new/default preferences: ON
    [InlineData(null, null, false, false)]    // a saved explicit OFF stays OFF
    [InlineData(null, null, true, true)]
    [InlineData(null, "0", null, false)]      // environment beats the default
    [InlineData(null, "0", true, false)]      // environment beats the setting
    [InlineData(null, "1", false, true)]
    [InlineData(null, "maybe", false, false)] // unknown environment values are ignored
    [InlineData(false, "1", true, false)]     // the test seam beats everything
    [InlineData(true, "0", false, true)]
    public void SmartCaptureDefaultsOnAndKeepsAnExplicitOff(bool? seam, string? env, bool? setting, bool expected) =>
        Assert.Equal(expected, WorkbenchRecorder.ResolveSmartCapture(seam, env, setting));

    [Fact]
    public void SavedOffSettingSurvivesAndMissingSettingIsOn()
    {
        string root = Temp(), previous = WorkbenchRecorder.PreferencesPath ?? "";
        var env = Environment.GetEnvironmentVariable("TSW_SMART_RECORDER");
        try
        {
            Environment.SetEnvironmentVariable("TSW_SMART_RECORDER", null);
            Directory.CreateDirectory(root);
            WorkbenchRecorder.PreferencesPath = Path.Combine(root, "settings.json");
            Assert.True(WorkbenchRecorder.SmartCapture);                       // no settings file at all
            WorkbenchPreferences.Update(WorkbenchRecorder.PreferencesPath, p => p with { RunView = "basic" });
            Assert.Null(WorkbenchPreferences.Load(WorkbenchRecorder.PreferencesPath).SmartRecorder);
            Assert.True(WorkbenchRecorder.SmartCapture);                       // settings file without the field
            Assert.True(WorkbenchRecorder.SaveSmartCapture(false));
            Assert.False(WorkbenchRecorder.SmartCapture);                      // explicit OFF
            Assert.Contains("\"SmartRecorder\": false", File.ReadAllText(WorkbenchRecorder.PreferencesPath).Replace("\"SmartRecorder\":false", "\"SmartRecorder\": false"));
            Environment.SetEnvironmentVariable("TSW_SMART_RECORDER", "1");
            Assert.True(WorkbenchRecorder.SmartCapture);                       // environment wins over the saved OFF
            Assert.True(WorkbenchRecorder.SmartCaptureForced);
            Environment.SetEnvironmentVariable("TSW_SMART_RECORDER", null);
            Assert.False(WorkbenchRecorder.SmartCapture);                      // and the saved OFF is unchanged
        }
        finally
        {
            Environment.SetEnvironmentVariable("TSW_SMART_RECORDER", env);
            WorkbenchRecorder.PreferencesPath = previous.Length == 0 ? null : previous; Delete(root);
        }
    }

    // ── decision 2: preselection follows the validated status and confidence ────────────────────────────────────────

    private static CheckpointProposal Proposal(string id, int after, long boundary, string confidence = Confidences.High, bool review = false,
        string placement = Placements.AfterStep) =>
        new("p-" + boundary, id, after, placement, placement == Placements.Unrepresentable ? null : ScenarioDocumentStep.Checkpoint(id),
            "modal-open", "state after input " + after, confidence, EvidenceLevels.HashOnly, after, boundary, null, ["c-" + boundary], [], review, []);

    private static AssertionSuggestion Suggestion(string proposal, string id, string confidence = Confidences.High, string status = SuggestionStatuses.Valid,
        bool defaultSelected = true) =>
        new(proposal, UiAssert.Modal(id, new ViewSelector(Type: "TDialog")) with { Severity = AssertionSeverity.Warning }, SuggestionOrigins.State, null,
            "modal-open", "A dialog was executing.", confidence, status, defaultSelected && status == SuggestionStatuses.Valid,
            status == SuggestionStatuses.Valid ? AssertionOutcomes.Pass : AssertionOutcomes.Fail, status == SuggestionStatuses.Valid ? null : "not checkable", []);

    private static RecordedInput KeyAt(long sequence, ushort code, string? text = null) =>
        new(sequence, "backend-key", sequence * 100, "key", Events.evKeyDown, code, text, 0, 0, 0, 0, 0, 80, 25);

    private static RecordingReviewModel Model(params (CheckpointProposal Proposal, AssertionSuggestion[] Suggestions, bool CaptureOnly)[] items)
    {
        var conversion = RecordingConverter.Convert([KeyAt(1, Keys.kbF3), KeyAt(2, Keys.kbF2), KeyAt(3, Keys.kbEnter), KeyAt(4, Keys.kbEsc)], 80, 25);
        var plan = new CheckpointPlan(conversion.Steps, true, items.Select(i => i.Proposal).ToArray(), []);
        var analysis = new SmartRecordingAnalysis(new DetectionResult([], [], new DetectorCounters(0, 0, 0, 0, 0, 0, 0, 0, 0)), [], plan, []);
        CheckpointEvidence None(CheckpointProposal p) => new(p.Id, p.BoundaryId, 1, p.AfterSequence, EvidenceSources.None, null, null, null, false, []);
        return new RecordingReviewModel(analysis, items.Select(i => new CheckpointSuggestions(i.Proposal, None(i.Proposal), i.Suggestions, i.CaptureOnly,
            i.CaptureOnly ? "capture only" : null, 80, 25)).ToArray(), conversion, []);
    }

    [Fact]
    public void OnlySafeHighConfidenceItemsArePreselected()
    {
        var high = Proposal("dialog-open", 0, 1);
        var medium = Proposal("medium", 1, 2, Confidences.Medium);
        var needsReview = Proposal("needs-review", 1, 3, review: true);
        var noSafe = Proposal("no-safe", 2, 4);
        var lost = Proposal("lost", 2, 5, placement: Placements.Unrepresentable, review: true);
        var capture = Proposal("capture", 3, 6);
        var m = Model(
            (high, [Suggestion(high.Id, "a-high"), Suggestion(high.Id, "a-medium", Confidences.Medium), Suggestion(high.Id, "a-low", Confidences.Low),
                Suggestion(high.Id, "a-unsupported", status: SuggestionStatuses.Unsupported), Suggestion(high.Id, "a-withheld", status: SuggestionStatuses.Rejected),
                Suggestion(high.Id, "a-over-cap", defaultSelected: false)], false),
            (medium, [Suggestion(medium.Id, "b-high")], false),
            (needsReview, [Suggestion(needsReview.Id, "c-high")], false),
            (noSafe, [Suggestion(noSafe.Id, "d-medium", Confidences.Medium)], false),
            (lost, [Suggestion(lost.Id, "e-high")], false),
            (capture, [], true));
        var cp = m.Checkpoints.ToDictionary(c => c.CheckpointId);
        Assert.True(cp["dialog-open"].Accepted && cp["dialog-open"].Preselected);
        Assert.Equal(["a-high"], cp["dialog-open"].Assertions.Where(a => a.Selected).Select(a => a.Id));
        Assert.All(cp["dialog-open"].Assertions.Where(a => a.Id != "a-high"), a => Assert.False(a.Preselected));
        // A medium checkpoint, one that needs review, one without a safe assertion, an unrepresentable one and a capture-only one stay unaccepted.
        foreach (string id in new[] { "medium", "needs-review", "no-safe", "lost", "capture" }) Assert.False(cp[id].Accepted, id);
        // Their safe assertions are still selected, but inactive until the checkpoint is accepted (nothing of theirs is inserted).
        Assert.True(cp["medium"].Assertions.Single().Selected);
        var materialized = RecordingReviewMaterializer.Materialize(m, []);
        Assert.True(materialized.Ok);
        Assert.Equal(1, materialized.Checkpoints);
        Assert.Equal(1, materialized.Assertions);
        // The user can deselect everything; Save/Restore (Cancel) brings the preselection back.
        var saved = m.Save();
        Assert.Null(RecordingReviewModel.SetSelected(cp["dialog-open"].Assertions[0], false));
        Assert.Null(m.SetAccepted(cp["dialog-open"], false));
        Assert.False(m.AnyAccepted);
        Assert.Equal(m.Conversion.DocumentSteps.Select(s => s.ToString()), RecordingReviewMaterializer.Materialize(m, []).Steps.Select(s => s.ToString()));
        m.Restore(saved);
        Assert.True(cp["dialog-open"].Accepted);
        Assert.True(cp["dialog-open"].Assertions[0].Selected);
        // Unsafe items can never be selected by the user either.
        Assert.NotNull(RecordingReviewModel.SetSelected(cp["dialog-open"].Assertions.Single(a => a.Id == "a-withheld"), true));
        Assert.NotNull(m.SetAccepted(cp["lost"], true));
        Assert.Contains("1 preselected", m.Summary()[0]);
    }

    [Fact]
    public void PendingCountsSeparateUnacceptedFromNotInsertable()
    {
        var high = Proposal("dialog-open", 0, 1);
        var medium = Proposal("medium", 1, 2, Confidences.Medium);
        var lost = Proposal("lost", 2, 3, placement: Placements.Unrepresentable, review: true);
        var m = Model((high, [Suggestion(high.Id, "a-high"), Suggestion(high.Id, "a-medium", Confidences.Medium),
                Suggestion(high.Id, "a-unsupported", status: SuggestionStatuses.Unsupported)], false),
            (medium, [Suggestion(medium.Id, "b-high")], false), (lost, [], true));
        Assert.Equal(1, m.PendingCheckpoints);           // medium: could be accepted
        Assert.Equal(1, m.PendingAssertions);            // a-medium in the accepted checkpoint
        Assert.Equal(1, m.UnrepresentableCheckpoints);   // lost: never insertable
        Assert.Equal(1, m.NotInsertableAssertions);      // a-unsupported: never insertable
        Assert.True(m.AnyPending);
        m.AcceptAll();
        foreach (var a in m.Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Selectable)) RecordingReviewModel.SetSelected(a, true);
        Assert.False(m.AnyPending);                      // only the never-insertable items are left out
        m.RejectAll();
        Assert.Equal(2, m.PendingCheckpoints);
    }

    // ── decisions 3 and 4: the recording window ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(120, 36)]
    [InlineData(140, 40)]
    [InlineData(160, 50)]
    public async Task ReviewOpensOnceAfterUserStopAndUnacceptedProposalsAreConfirmed(int width, int height)
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(width, height, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                WorkbenchRecorder.SmartCaptureOverride = true;
                int opened = 0;
                WorkbenchRecordingWindow.ReviewOpener = (_, model) => { opened++; return true; };

                // A stopped recording attached by a test (not a user Stop): no automatic review.
                var window = await OpenStopped(bridge, editor);
                await Settle(bridge);
                Assert.Equal(0, opened);
                // The user's Stop: the review opens once, from the event loop, after the target was closed.
                await bridge.Invoke(() => window.StoppedByUserForTesting());
                await Until(bridge, () => opened == 1);
                Assert.True(await Invoke(bridge, () => window.AutoReviewed));
                await bridge.Invoke(() => window.UpdateView()); await Settle(bridge);
                Assert.Equal(1, opened);
                await Shot(bridge, "stopped-auto-reviewed");

                // Decision 4: Append with unaccepted proposals asks; "Back to Review" reopens the review and inserts nothing.
                var snapshot = await Invoke(bridge, () => Steps(editor));
                string? title = null, text = null; string[]? options = null; int defaultChoice = -9;
                int answer = 0;
                WorkbenchRecordingWindow.Chooser = (t, x, o, d) => { title = t; text = x; options = o; defaultChoice = d; return answer; };
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.AppendCommand));
                Assert.Equal(WorkbenchRecordingWindow.PendingTitle, title);
                Assert.Equal(0, defaultChoice);                                   // the safe default goes back to Review
                Assert.Equal(["~B~ack to Review", "~I~nsert accepted only", "Cancel"], options);
                Assert.Contains("NOT be inserted", text);
                Assert.Contains("Only the recorded input steps will be inserted.", text);
                if (await Invoke(bridge, () => window.Review!.UnrepresentableCheckpoints + window.Review.NotInsertableAssertions) > 0) Assert.Contains("Not insertable in any case", text);
                else Assert.DoesNotContain("Not insertable", text);
                Assert.Equal(2, opened);
                Assert.Equal(snapshot, await Invoke(bridge, () => Steps(editor)));
                Assert.NotNull(await Invoke(bridge, () => WorkbenchRecordingWindow.Active));

                // Cancel: no mutation, the window and the decisions stay.
                answer = -1;
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.InsertCommand));
                Assert.Equal(snapshot, await Invoke(bridge, () => Steps(editor)));
                Assert.Contains("Nothing was inserted", app.Notifications.History[^1].Message);

                // Replace with pending proposals: the same dialog is the Replace confirmation; Cancel removes nothing and
                // the separate Replace confirmation is never shown.
                await bridge.Invoke(() => editor.SelectSteps(0, 1));
                WorkbenchRecordingWindow.ConfirmReplace = _ => throw new InvalidOperationException("Replace must be confirmed by the pending dialog.");
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.ReplaceCommand));
                Assert.Contains("Replace removes the 1 selected step", text);
                Assert.Equal("~R~eplace, accepted only", options![1]);
                Assert.Equal(snapshot, await Invoke(bridge, () => Steps(editor)));

                // Proceed with accepted only: exactly the converter's steps (nothing was accepted).
                answer = 1;
                var legacy = await Invoke(bridge, () => window.Conversion!.DocumentSteps.Select(s => s.ToString()).ToArray());
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.AppendCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Equal(legacy, await Invoke(bridge, () => Steps(editor).Skip(snapshot.Length).ToArray()));

                // Everything accepted (nothing pending): no dialog at all.
                window = await OpenStopped(bridge, editor);
                title = null;
                await bridge.Invoke(() =>
                {
                    window.Review!.AcceptAll();
                    foreach (var a in window.Review.Checkpoints.SelectMany(c => c.Assertions).Where(a => a.Selectable)) RecordingReviewModel.SetSelected(a, true);
                    window.Execute(WorkbenchRecordingWindow.AppendCommand);
                });
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Null(title);

                // Smart capture OFF: no proposals, no dialog, no automatic review.
                WorkbenchRecorder.SmartCaptureOverride = false;
                window = await OpenStopped(bridge, editor, review: false);
                await bridge.Invoke(() => window.StoppedByUserForTesting()); await Settle(bridge);
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.AppendCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Null(title);
                Assert.Equal(2, opened);
            });
        }
        finally
        {
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchRecordingWindow.Chooser = null; WorkbenchRecordingWindow.ReviewOpener = null;
            WorkbenchRecordingWindow.ConfirmReplace = null; WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    [Fact]
    public async Task NoAutomaticReviewAfterTargetExitCancelFailureOrLegacyCapture()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                WorkbenchRecorder.SmartCaptureOverride = true;
                int opened = 0, asked = 0;
                WorkbenchRecordingWindow.ReviewOpener = (_, _) => { opened++; return true; };
                WorkbenchRecordingWindow.Chooser = (_, _, _, _) => { asked++; return -1; };
                RecordedInput[] events = [KeyAt(1, Keys.kbF3), KeyAt(2, Keys.kbEnter)];

                foreach (var state in new[] { RecordingState.Cancelled, RecordingState.Failed, RecordingState.Recording, RecordingState.Paused, RecordingState.Stopping })
                {
                    await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                    var w = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                    var s = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", state, events);
                    s.Capture.Negotiate([.. Protocol.RecordFeatures.All, Protocol.RecordFeatures.Picks]);
                    await bridge.Invoke(() => { w.Attach(s); w.StoppedByUserForTesting(); });
                    await Settle(bridge);
                    Assert.Equal(0, opened);
                    await bridge.Invoke(() => { w.Attach(RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Cancelled, [])); w.Execute(WorkbenchRecordingWindow.DiscardCommand); });
                    await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                }

                // Legacy capture (old Proxy negotiated nothing): zero proposals, so no review even after a user Stop.
                await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                var legacy = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                await bridge.Invoke(() => { legacy.Attach(RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Stopped, events)); legacy.StoppedByUserForTesting(); });
                await Settle(bridge);
                Assert.Equal(0, opened);
                Assert.Contains("Smart capture was unavailable", await Text(bridge, legacy));

                // The target ended the recording (Ended set): proposals may exist but the review is not forced open.
                var ended = await OpenStopped(bridge, editor, ended: true);
                await bridge.Invoke(() => ended.StoppedByUserForTesting()); await Settle(bridge);
                Assert.Equal(0, opened);
                Assert.True(await Invoke(bridge, () => TView.CommandEnabled(WorkbenchRecordingWindow.ReviewCommand)));
                await bridge.Invoke(() => ended.Execute(WorkbenchRecordingWindow.DiscardCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                Assert.Equal(0, asked);
            });
        }
        finally
        {
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchRecordingWindow.Chooser = null; WorkbenchRecordingWindow.ReviewOpener = null;
            WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    // ── decision 5: stale Proxy before Start and at the handshake ─────────────────────────────────────────────────────

    [Fact]
    public async Task StaleProxyIsNeverUsedSilently()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            string settings = Path.Combine(root, "settings.json");
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                WorkbenchRecorder.SmartCaptureOverride = null;
                if (WorkbenchRecorder.SmartCaptureForced is not null) return;    // an environment override would hide the setting path
                string before = File.Exists(settings) ? File.ReadAllText(settings) : "";
                var probe = new ProxyProbeResult(ProxyCapability.Stale, "The deployed Workbench Proxy predates Smart capture (no Smart Recorder features).");
                RecordingProxyProbe.Override = _ => probe;
                var started = new List<RecordingSession>();
                int answer = -1, asked = 0; string? title = null, text = null; string[]? options = null;
                WorkbenchRecordingWindow.Chooser = (t, x, o, _) => { asked++; title = t; text = x; options = o; return answer; };
                var fixedFor = new List<string>();
                WorkbenchRecordingWindow.FixIssues = exe => { fixedFor.Add(exe); probe = new(ProxyCapability.Capable, "updated"); return true; };

                async Task<WorkbenchRecordingWindow> Open()
                {
                    await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
                    var w = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
                    w.SessionFactory = backend =>
                    {
                        var s = RecordingSession.ForTesting(backend, "80x25", RecordingState.Ready, []);
                        started.Add(s); return s;
                    };
                    return w;
                }
                async Task Close(WorkbenchRecordingWindow w)
                {
                    await bridge.Invoke(() => { if (w.Session is { } s) w.Attach(RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Cancelled, [])); w.Execute(WorkbenchRecordingWindow.DiscardCommand); });
                    await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
                }

                // Cancel: nothing starts, Smart capture setting untouched.
                var window = await Open();
                Assert.True(await Invoke(bridge, () => window.SmartCaptureOn));
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StartCommand));
                Assert.Equal(WorkbenchRecordingWindow.StaleProxyTitle, title);
                Assert.Equal(WorkbenchRecordingWindow.StaleProxyOptions, options);
                Assert.Contains("predates Smart capture", text);
                Assert.Contains("asks for your approval first (a backup is kept)", text);
                Assert.Empty(started);
                Assert.Null(await Invoke(bridge, () => window.Session));
                await Shot(bridge, "ready-after-stale-cancel");

                // Start inputs only: a deliberate, visible choice for this recording only; the setting stays ON.
                answer = 1;
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StartCommand));
                var inputsOnly = Assert.Single(started);
                Assert.False(inputsOnly.RequireSmartCapture);
                Assert.False(inputsOnly.ModifierPicks);
                Assert.True(await Invoke(bridge, () => window.InputsOnlyChosen));
                Assert.False(await Invoke(bridge, () => window.SmartCaptureOn));
                Assert.Equal(before, File.Exists(settings) ? File.ReadAllText(settings) : "");
                Assert.True(WorkbenchRecorder.SmartCapture);
                await Close(window);

                // Fix Issues: the existing consent + backup path is opened for the target; nothing starts by itself.
                answer = 0;
                window = await Open();
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StartCommand));
                Assert.Equal([TestWorkbenchHostTests.SampleTarget], fixedFor);
                Assert.Single(started);
                Assert.Contains("Press Start to record with Smart capture", app.Notifications.History[^1].Message);
                // After the fix the probe reports the capability: Start records with Smart capture, no dialog.
                asked = 0;
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StartCommand));
                Assert.Equal(0, asked);
                Assert.Equal(2, started.Count);
                Assert.True(started[1].RequireSmartCapture);
                await Close(window);

                // Fix Issues declined/unavailable: a warning, still nothing recorded.
                probe = new(ProxyCapability.Stale, "stale again");
                WorkbenchRecordingWindow.FixIssues = _ => false;
                window = await Open();
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.StartCommand));
                Assert.Equal(2, started.Count);
                Assert.Contains("Fix Issues is not available here", app.Notifications.History[^1].Message);

                // The runtime handshake guard: Smart requested, the Proxy negotiated nothing -> the session failed before
                // recording; the window offers the same choice (Cancel here) and clears the failed session.
                answer = -1; asked = 0;
                var failed = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Failed, []);
                failed.SmartUnavailable = true;
                await bridge.Invoke(() => window.Attach(failed));
                await Until(bridge, () => asked == 1);
                Assert.Contains("has no Smart capture", text);
                Assert.Null(await Invoke(bridge, () => window.Session));
                await Close(window);
            });
        }
        finally
        {
            RecordingProxyProbe.Override = null; WorkbenchRecordingWindow.Chooser = null; WorkbenchRecordingWindow.FixIssues = null;
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    [Fact]
    public void CapabilityProbeReadsDeployedMetadataOnly()
    {
        string root = Temp();
        try
        {
            // This build's payload (the test output folder holds this Workbench's Testing and Proxy assemblies): Capable.
            string here = Path.GetDirectoryName(typeof(Protocol).Assembly.Location)!;
            string current = Path.Combine(root, "current");
            Directory.CreateDirectory(current);
            // The Proxy is not referenced by the tests; take it from this build's Proxy output (artifacts layout: bin/<project>/<config>).
            string proxy = new[] { Path.Combine(here, "..", "..", "TSharpWorkbench", Path.GetFileName(here), "proxy-payload"), Path.Combine(here, "..", "..", "TSharpWorkbench.Proxy", Path.GetFileName(here)) }
                .Select(Path.GetFullPath).First(d => File.Exists(Path.Combine(d, TargetDiagnostics.ProxyFile)));
            File.Copy(Path.Combine(here, TargetDiagnostics.TestingFile), Path.Combine(current, TargetDiagnostics.TestingFile));
            File.Copy(Path.Combine(proxy, TargetDiagnostics.ProxyFile), Path.Combine(current, TargetDiagnostics.ProxyFile));
            Assert.Equal(ProxyCapability.Capable, RecordingProxyProbe.Probe(Path.Combine(current, "App.exe")).Capability);

            // No deployed Proxy: Unknown (the handshake decides).
            string empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
            Assert.Equal(ProxyCapability.Unknown, RecordingProxyProbe.Probe(Path.Combine(empty, "App.exe")).Capability);

            // Not a managed assembly: Unknown, never Capable.
            string junk = Path.Combine(root, "junk"); Directory.CreateDirectory(junk);
            File.WriteAllText(Path.Combine(junk, TargetDiagnostics.TestingFile), "not a PE file");
            File.WriteAllText(Path.Combine(junk, TargetDiagnostics.ProxyFile), "not a PE file");
            Assert.Equal(ProxyCapability.Unknown, RecordingProxyProbe.Probe(Path.Combine(junk, "App.exe")).Capability);

            // A pre-Smart-Recorder payload, when one is available on this machine (coordinator evidence; read-only).
            string? stale = Environment.GetEnvironmentVariable("TSWB_STALE_PROXY_TARGET");
            if (stale is { Length: > 0 } && File.Exists(stale))
                Assert.Equal(ProxyCapability.Stale, RecordingProxyProbe.Probe(stale).Capability);
        }
        finally { Delete(root); }
    }

    // ── decision 6: Windows Terminal limitation is stated, not hidden ─────────────────────────────────────────────────

    [Fact]
    public async Task ConsoleStatusNamesTheWindowsTerminalLimitationAndPick()
    {
        string root = Temp(), home = Path.Combine(root, "home");
        var previous = WorkbenchScenarioEditorWindow.RecordRequested;
        try
        {
            string project = SaveProject(root, new WorkbenchProject { Name = "Recorder", Target = TestWorkbenchHostTests.SampleTarget, OutputRoot = Path.Combine(root, "runs") });
            await Drive(120, 36, root, home, async (app, bridge) =>
            {
                await Wait(bridge, () => app.Project is { Busy: false } p0 && p0.TestList.Strings.Count >= 1 ? p0 : null, 60);
                await bridge.Invoke(() => app.ActivateProject(WorkbenchProject.Load(project), project));
                var main = await Wait(bridge, () => app.Project is { Busy: false, ProjectPath: not null } p ? p : null, 60);
                await bridge.Invoke(() => WorkbenchRecorder.Install(app));
                var editor = await Invoke(bridge, () => main.OpenScenarioEditor(SavedDocument(root))!);
                WorkbenchRecorder.SmartCaptureOverride = true;
                WorkbenchRecordingWindow.ReviewOpener = (_, _) => true;
                var window = await OpenStopped(bridge, editor);
                var status = await Invoke(bridge, () => window.Review!.Status.ToArray());
                string console = Assert.Single(status, l => l.StartsWith("Console window:", StringComparison.Ordinal));
                Assert.Contains("with injected input", console);
                Assert.Contains("Windows Terminal may take Ctrl+Shift+click", console);
                Assert.Contains("use Pick", console);
                Assert.DoesNotContain("physical", console);
                await bridge.Invoke(() => window.Execute(WorkbenchRecordingWindow.DiscardCommand));
                await Until(bridge, () => WorkbenchRecordingWindow.Active is null);
            });
        }
        finally
        {
            WorkbenchRecorder.SmartCaptureOverride = null; WorkbenchRecordingWindow.ReviewOpener = null;
            WorkbenchScenarioEditorWindow.RecordRequested = previous; Delete(root);
        }
    }

    // ── harness (pattern of RecorderIntegrationTests) ───────────────────────────────────────────────────────────

    private static string[] Steps(WorkbenchScenarioEditorWindow editor) => editor.Document.Steps.Select(s => s.ToString()).ToArray();

    private static RecordedBoundary Mark(long id, long after, ulong hash, string? modal = null) =>
        new(id, Protocol.BoundaryKinds.Idle, id == 1 ? Protocol.BoundaryCauses.Start : Protocol.BoundaryCauses.Input, after, after, id, hash, id * 100, 80, 25, modal, 0);

    private static async Task<WorkbenchRecordingWindow> OpenStopped(Bridge bridge, WorkbenchScenarioEditorWindow editor, bool review = true, bool ended = false)
    {
        await bridge.Invoke(() => WorkbenchScenarioEditorWindow.RecordRequested!(editor));
        var window = await Wait(bridge, () => WorkbenchRecordingWindow.Active);
        RecordedInput[] events = [KeyAt(1, Keys.kbF3), KeyAt(2, 0x1e61, "a"), KeyAt(3, Keys.kbEnter)];
        var stopped = RecordingSession.ForTesting("Win32ConsoleDriver", "80x25", RecordingState.Stopped, events);
        stopped.Capture.Negotiate([.. Protocol.RecordFeatures.All, Protocol.RecordFeatures.Picks]);
        stopped.Capture.Merge(new RecordingBatch([], 3, false, false, 0, 0, 0, 0, 80, 25, "Win32ConsoleDriver")
        {
            Boundaries = [Mark(1, 0, 1), Mark(2, 1, 2, modal: "TDialog"), Mark(3, 2, 3, modal: "TDialog"), Mark(4, 3, 4)], BoundaryNext = 4,
            Coverage = new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        });
        if (ended) typeof(RecordingSession).GetProperty("Ended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(stopped, "The target ended the recording (test).");
        await bridge.Invoke(() => window.Attach(stopped));
        if (review) await Wait(bridge, () => window.Review);
        return window;
    }

    private static async Task Settle(Bridge bridge) { for (int i = 0; i < 4; i++) { await bridge.Invoke(() => { }); await Task.Delay(30); } }

    private static string SavedDocument(string root)
    {
        string path = Path.Combine(root, "docs", "recorded-" + Guid.NewGuid().ToString("N")[..6] + ".scenario.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = ScenarioDocument.New("recorded/demo", "Recorded demo", "Recorder window capture document.", checkpoint: "initial").Save(path);
        Assert.True(saved.Saved, saved.Message);
        return path;
    }
    private static string Row(ScreenSnapshot s, int y) => new(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character == 0 ? ' ' : s[x, y].Character).ToArray());
    private static string Screen(ScreenSnapshot s) => string.Join("\n", Enumerable.Range(0, s.Height).Select(y => Row(s, y).TrimEnd()));
    private static string SaveProject(string folder, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, "recorder.twproj"); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "layout-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<string> Text(Bridge bridge, WorkbenchRecordingWindow window) { string text = ""; await bridge.Invoke(() => { window.UpdateView(); text = window.Text; }); return text; }
    private static async Task<T> Invoke<T>(Bridge bridge, Func<T> func) { T result = default!; await bridge.Invoke(() => result = func()); return result; }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            string target = TestWorkbenchHostTests.SampleTarget;
            using var app = new WorkbenchApp(target, output, new(target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") });
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { WorkbenchRecordingWindow.Active?.Close(); app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(30));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(300)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }

    private static async Task Shot(Bridge bridge, string state)
    {
        await bridge.Invoke(() => { }); await Task.Delay(80); await bridge.Invoke(() => { });
        if (Evidence is not { } root) return;
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root);
            var screen = ScreenSnapshot.CopyRoot();
            string name = $"layout-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical.png"), png);
            File.WriteAllText(Path.Combine(root, name + ".logical.txt"), Screen(screen));
            File.WriteAllText(Path.Combine(root, name + ".logical.json"), JsonSerializer.Serialize(new
            {
                name, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical", driver = "NullDriver", test = nameof(RecorderUxTests),
                renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
    private static Task Until(Bridge bridge, Func<bool> predicate, int seconds = 30) => Wait(bridge, () => predicate() ? "" : null, seconds);
    private static async Task<T> Wait<T>(Bridge bridge, Func<T?> predicate, int seconds = 30) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { T? value = null; await bridge.Invoke(() => value = predicate()).WaitAsync(timeout.Token); if (value is not null) return value; await Task.Delay(20, timeout.Token); }
    }
    private sealed class Bridge : TView
    {
        private sealed class Work(Action action) : IInfo { internal Action Action { get; } = action; }
        internal Bridge() : base(new TRect(0, 0, 0, 0)) { options = 0; state &= unchecked((ushort)~Views.sfVisible); }
        internal Task Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(0x7ddd, new Work(() => { try { action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); } }));
            return done.Task;
        }
        public override void HandleEvent(ref TEvent ev)
        { if (ev.What == Events.evCommand && ev.message.command == 0x7ddd && ev.message.infoPtr is Work work) { ClearEvent(ref ev); work.Action(); } else base.HandleEvent(ref ev); }
    }
}
