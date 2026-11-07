using System.Text.Json;
using TSharpVision;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;

// One baseline candidate bound at the moment the review opens: provenance for the dialog and the hashes that are
// re-checked before anything is written. Loading validates integrity and refuses what the store must never receive.
internal sealed record CandidateReview(string RunRoot, string RunName, string Key, VisualBaselineFile File, string MetadataPath, string MetadataHash,
    string CellsPath, string CellsHash, string ManifestPath, string ApprovedCells, bool Replace, string CurrentState, string? CurrentHash, string? CandidatePng,
    string ApprovedMetadata, BaselineEnvironment Environment)
{
    internal static CandidateReview Load(BatchResult batch, VisualBaselineSet set, string key)
    {
        string metadataPath, cellsPath;
        try { metadataPath = WorkbenchDialog.SafeArtifact(batch.RunRoot, key + ".candidate.json"); cellsPath = WorkbenchDialog.SafeArtifact(batch.RunRoot, key + ".candidate.tvs1"); }
        catch (FileNotFoundException ex) { throw new ArgumentException($"The candidate file {Path.GetFileName(ex.FileName)} is missing from the run folder; it may have been deleted after the run. Nothing can be approved."); }
        byte[] metadata = System.IO.File.ReadAllBytes(metadataPath);
        VisualBaselineFile file;
        try { file = JsonSerializer.Deserialize<VisualBaselineFile>(metadata, ScenarioRunner.Json) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new ArgumentException("The candidate metadata cannot be read: " + ex.Message); }
        if (file.Key != key || file.Status != "candidate") throw new ArgumentException($"{Path.GetFileName(metadataPath)} is not a candidate for {key}.");
        if (!file.CaptureLayer.StartsWith("logical", StringComparison.Ordinal))
            throw new ArgumentException($"The candidate's capture layer ({file.CaptureLayer}) is not supported: only logical cell baselines can be approved.");
        string cellsHash = ScreenSnapshot.Hash(System.IO.File.ReadAllBytes(cellsPath));
        if (cellsHash != file.CellsSha256) throw new ArgumentException("The candidate cells failed their integrity check (SHA-256 differs from the candidate record). Nothing can be approved.");
        string gridKey = file.Grid == file.InitialGrid ? file.Grid : file.Grid + "-from-" + file.InitialGrid;
        var entry = set.Find(file.ScenarioId, file.CheckpointId, gridKey) ?? throw new ArgumentException("This checkpoint is no longer gated by the baseline store; nothing can be approved.");
        // The candidate's own environment decides the slot; another environment's baseline is never replaced.
        var environment = BaselineEnvironment.Of(file);
        var slot = set.Slot(key, environment);
        var (status, reason, _, _, sha) = set.LoadApproved(entry, gridKey, environment);
        bool replace = slot.Exists;
        string state = !replace ? $"MISSING: no approved baseline exists for this checkpoint in {environment}."
            : status == VisualStatuses.Match ? $"APPROVED baseline exists (cells {Brief(sha)}); approving replaces it."
            : $"{status}: {reason} Approving replaces the existing files.";
        string png = key + ".candidate.png";
        return new(batch.RunRoot, Path.GetFileName(batch.RunRoot), key, file, metadataPath, ScreenSnapshot.Hash(metadata), cellsPath, cellsHash,
            set.ManifestPath, slot.Cells, replace, state, sha, ResultText.Exists(batch.RunRoot, png) ? Path.Combine(batch.RunRoot, png) : null, slot.Metadata, environment);
    }
    internal static string Short(string? hash) => hash is null ? "none" : hash.Length > 20 ? hash[..12] + "..." + hash[^8..] : hash;
    private static string Brief(string? hash) => hash is null ? "none" : hash.Length > 12 ? hash[..8] + ".." + hash[^4..] : hash;
    internal string Operation => Replace ? "Replace" : "Approve";
    // The candidate and the store are exactly as when the review opened; otherwise approval must not proceed.
    internal string? Changed(VisualBaselineSet set)
    {
        if (!System.IO.File.Exists(MetadataPath) || !System.IO.File.Exists(CellsPath)) return "The candidate files were removed since the review opened.";
        if (ScreenSnapshot.Hash(System.IO.File.ReadAllBytes(MetadataPath)) != MetadataHash || ScreenSnapshot.Hash(System.IO.File.ReadAllBytes(CellsPath)) != CellsHash)
            return "The candidate changed since the review opened. Review it again.";
        var slot = set.Slot(Key, Environment);
        bool exists = slot.Exists;
        string? sha = System.IO.File.Exists(slot.Cells) ? ScreenSnapshot.Hash(System.IO.File.ReadAllBytes(slot.Cells)) : null;
        if (slot.Cells != ApprovedCells) return "The baseline store layout changed since the review opened. Review it again.";
        if (exists != Replace || sha != CurrentHash) return "The approved baseline changed since the review opened. Review it again.";
        return null;
    }
}

// Approve Baseline Candidate / Replace Approved Baseline (): one modal dialog for one checkpoint. The mode
// follows the store. The action button stays disabled until the confirmation check box is ticked; Cancel is the
// default, and Esc, the close icon and Enter on any other control cancel without writing anything.
internal sealed class WorkbenchCandidateDialog : TDialog
{
    internal const ushort OpenImage = 0x7d7a;
    internal const string ApproveTitle = "Approve Baseline Candidate", ReplaceTitle = "Replace Approved Baseline";
    private readonly Action<string> open;
    private readonly CandidateReview review;
    internal TCheckBoxes Confirm { get; }
    internal TButton ActionButton { get; }
    internal TButton CancelButton { get; }
    internal string Text { get; }

    internal static TRect Bounds(TDeskTop desktop) => new(0, 0, Math.Min(108, desktop.size.x - 2), Math.Min(26, desktop.size.y));

    internal WorkbenchCandidateDialog(TDeskTop desktop, CandidateReview review, string store, Action<string> open, bool projectOwned = false)
        : base(Bounds(desktop), review.Replace ? ReplaceTitle : ApproveTitle)
    {
        this.review = review; this.open = open;
        options |= Views.ofCentered; helpCtx = WorkbenchBaselineExplorer.Context;
        int w = size.x, h = size.y, inner = w - 8;
        var f = review.File;
        string Fit(string s) => WorkbenchText.Fit(s, inner);
        string masked = f.Normalizations.Length == 0 ? "none" : string.Join(", ", f.Normalizations.Select(n => $"{n.Rule} ({n.Spans.Length} span{(n.Spans.Length == 1 ? "" : "s")} masked)"));
        string[] provenance =
        [
            f.ExecutionScenarioId is { } executed ? $"Scenario       {f.ScenarioId} (baseline identity; executed as {executed})" : $"Scenario       {f.ScenarioId}",
            $"Checkpoint     {f.CheckpointId} at {f.Grid} (requested grid {f.InitialGrid})",
            $"Baseline key   {review.Key}",
            $"Store          {store}",
            $"Current state  {review.CurrentState}",
            $"Candidate      from {review.RunName}; target {f.Target ?? "not recorded"}",
            $"               cells SHA-256 {CandidateReview.Short(f.CellsSha256)} (integrity verified)",
            $"Environment    {review.Environment}   ({f.CaptureLayer})",
            $"Normalization  policy {CandidateReview.Short(f.NormalizationFingerprint)}: {masked}",
        ];
        var details = new TGroupBox(new(2, 2, w - 2, 13), "Candidate");
        details.Insert(new TStaticText(new(2, 1, w - 6, 10), string.Join('\n', provenance.Select(Fit))));
        // Text-only sections are never Tab stops: Tab moves between the check box and the buttons.
        details.options &= unchecked((ushort)~Views.ofSelectable); Insert(details);
        string destination = WorkbenchText.Path(review.ApprovedCells, inner - 4);
        string[] consequence = review.Replace
            ? ["Overwrites the approved baseline of this one checkpoint with this candidate:", "  " + destination,
               "Later Visual regression runs in this environment compare with it. No other baseline or environment changes.",
               projectOwned ? "The prior project store is retained. Save commits the new approved reference." : "The external store keeps no copy of replaced files."]
            : ["Writes this one candidate into the store as the approved baseline of this checkpoint:", "  " + destination,
               "Later Visual regression runs in this environment compare with it. No other baseline or environment changes.",
               projectOwned ? "Save commits this approved reference. Historical run verdicts stay unchanged." : "Keep the external store under version control."];
        var effect = new TGroupBox(new(2, 13, w - 2, 19), review.Replace ? "What replacing does" : "What approving does");
        effect.Insert(new TStaticText(new(2, 1, w - 6, 5), string.Join('\n', consequence.Select(Fit))));
        effect.options &= unchecked((ushort)~Views.ofSelectable); Insert(effect);
        Insert(new TStaticText(new(3, 19, w - 3, 20), Fit("Open the candidate image first if you have not inspected it in the Results or Comparison window.")));
        Confirm = new(new(3, h - 5, w - 3, h - 4), new TSItem(review.Replace
            ? "~I~ inspected this candidate and want it to replace the approved baseline."
            : "~I~ inspected this candidate and want it to become the approved baseline.", null!));
        Insert(Confirm);
        var image = new TButton(new(3, h - 3, 29, h - 1), "~O~pen candidate image", OpenImage, ButtonConstants.bfNormal); Insert(image);
        if (review.CandidatePng is null) image.SetState(Views.sfDisabled, true);
        // Owner spacing rule: one empty row above the buttons; the shadow row stays above the bottom frame.
        ActionButton = new(new(w - 34, h - 3, w - 19, h - 1), review.Replace ? "~R~eplace" : "~A~pprove", Views.cmYes, ButtonConstants.bfNormal);
        ActionButton.SetState(Views.sfDisabled, true); Insert(ActionButton);
        CancelButton = new(new(w - 17, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfDefault); Insert(CancelButton);
        Text = string.Join("\n", provenance.Concat(consequence));
        Confirm.Select();
    }

    private void Refresh()
    {
        bool confirmed = Confirm.value == 1;
        if (ActionButton.GetState(Views.sfDisabled) != confirmed) return;
        ActionButton.SetState(Views.sfDisabled, !confirmed); ActionButton.DrawView();
    }
    // Approve/Replace needs the ticked confirmation even if the command arrives some other way.
    public override bool Valid(ushort command) => command != Views.cmYes || Confirm.value == 1 ? base.Valid(command) : false;
    public override void HandleEvent(ref TEvent ev)
    {
        if (ev.What == Events.evCommand && ev.message.command == OpenImage)
        { ClearEvent(ref ev); if (review.CandidatePng is { } png) open(png); return; }
        base.HandleEvent(ref ev);
        Refresh();
    }
}

internal static class WorkbenchBaselineDialogs
{
    internal const string StoreStatusTitle = "Baseline Store Status";
    internal static string ConfirmTitle(bool replace) => replace ? "Replace Approved Baseline?" : "Approve Baseline?";

    // Concise final confirmation of the exact target and operation. Cancel is the default; Esc cancels.
    internal static bool Confirm(TDeskTop desktop, CandidateReview review, string store, bool projectOwned = false)
    {
        int w = Math.Min(84, desktop.size.x - 2), h = 13;
        string[] lines = review.Replace
            ? [$"Replace the approved baseline of {review.Key}", $"(cells {CandidateReview.Short(review.CurrentHash)}) with the candidate from {review.RunName}",
               $"(cells {CandidateReview.Short(review.CellsHash)})?", "", $"Store: {store}", projectOwned ? "Prior project baseline history is retained; Save commits the approved reference." : "The external store keeps no copy of replaced files."]
            : [$"Approve the candidate from {review.RunName} as the baseline of", review.Key, $"(cells {CandidateReview.Short(review.CellsHash)})?", "",
               $"Store: {store}", "Only this checkpoint changes."];
        using var dialog = new TDialog(new(0, 0, w, h), ConfirmTitle(review.Replace)) { helpCtx = WorkbenchBaselineExplorer.Context };
        dialog.options |= Views.ofCentered;
        dialog.Insert(new TStaticText(new(3, 2, w - 3, h - 4), string.Join('\n', lines.Select(l => WorkbenchText.Fit(l, w - 6)))));
        dialog.Insert(new TButton(new(3, h - 3, 18, h - 1), review.Replace ? "~R~eplace" : "~A~pprove", Views.cmYes, ButtonConstants.bfNormal));
        var cancel = new TButton(new(w - 17, h - 3, w - 3, h - 1), "~C~ancel", Views.cmCancel, ButtonConstants.bfDefault); dialog.Insert(cancel);
        cancel.Select();
        return desktop.ExecView(dialog) == Views.cmYes;
    }

    // Baseline Store Status (Baselines menu): read-only; the store, its state and counts, and one row per gated
    // checkpoint and grid. An unreadable store is shown as such instead of failing.
    internal static void StoreStatus(TDeskTop desktop, string manifest, string relative, int? runCandidates, WorkbenchNotifications notifications,
        BaselineEnvironment? environment = null)
    {
        var env = environment ?? BaselineEnvironment.Current();
        var rows = new List<string>();
        string state;
        try
        {
            var set = VisualBaselineSet.Load(manifest);
            var status = set.Status(env).ToArray();
            int approved = status.Count(s => s.Status == "Approved"), missing = status.Count(s => s.Status == VisualStatuses.BaselineMissing);
            int invalid = status.Length - approved - missing;
            int mapped = set.Manifest.Baselines.Where(e => e.ExecutionScenario is not null).Select(e => e.Scenario).Distinct().Count();
            state = $"valid manifest; {status.Length} gated checkpoint grid{(status.Length == 1 ? "" : "s")}: {approved} approved, {missing} missing" + (invalid > 0 ? $", {invalid} INVALID" : "")
                + (mapped > 0 ? $"; {mapped} explicit identity mapping{(mapped == 1 ? "" : "s")} (executionScenario)" : "");
            int key = Math.Clamp(status.Select(s => s.Key.Length).DefaultIfEmpty(20).Max() + 2, 20, 60);
            rows.Add(WorkbenchText.Column("Key", key) + WorkbenchText.Column("Status", 18) + "Reason");
            // The same status words as the Results Visual column (MISSING, INVALID, ...), never raw identifiers.
            static string Word(string s) => s == "Approved" ? "Approved" : ResultText.VisualColumn(s) is var v && v != "-" ? v : s.ToUpperInvariant();
            rows.AddRange(status.Select(s => WorkbenchText.Column(s.Key, key) + WorkbenchText.Column(Word(s.Status), 18) + s.Reason));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            state = System.IO.File.Exists(manifest) ? "! INVALID: the store cannot be read." : "! MISSING: the manifest file does not exist.";
            rows.Add(ex.Message.ReplaceLineEndings(" "));
            notifications.Warning("The baseline store cannot be read: " + ex.Message.ReplaceLineEndings(" "), ex.ToString());
        }
        int w = Math.Min(120, desktop.size.x - 2), h = Math.Min(30, desktop.size.y);
        using var dialog = new TDialog(new(0, 0, w, h), StoreStatusTitle) { helpCtx = WorkbenchBaselineExplorer.Context };
        dialog.options |= Views.ofCentered;
        var store = new TGroupBox(new(2, 1, w - 2, 6), "Store");
        store.Insert(new TStaticText(new(2, 1, w - 6, 4), string.Join('\n', new[]
        {
            "Manifest    " + WorkbenchText.Path(relative, w - 20) + "   |   environment " + env,
            "State       " + state,
            "Candidates  " + (runCandidates is { } n ? $"{n} kept in the selected run (Results > Review candidate...)" : "no run selected in Results")
        }.Select(l => WorkbenchText.Fit(l, w - 8)))));
        store.options &= unchecked((ushort)~Views.ofSelectable); dialog.Insert(store);
        var scroll = new TScrollBar(new(w - 3, 7, w - 2, h - 4)); dialog.Insert(scroll);
        var list = new TListBox(new(2, 7, w - 3, h - 4), 1, scroll); dialog.Insert(list);
        var collection = new TStringCollection(); foreach (string row in rows) collection.Insert(WorkbenchText.Fit(row, 400)); list.NewList(collection);
        var close = new TButton(new(w - 15, h - 3, w - 3, h - 1), "Close", Views.cmOK, ButtonConstants.bfDefault); dialog.Insert(close);
        list.Select();
        desktop.ExecView(dialog);
    }
}
