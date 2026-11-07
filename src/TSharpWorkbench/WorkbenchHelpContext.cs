using TSharpVision;

// IDs are part of the installed help format. Never renumber existing topics.
internal enum HelpTopic : ushort
{
    Index = 1,
    Start = 2,
    Workspace = 3,
    Properties = 4,
    Diagnostics = 5,
    Scenarios = 6,
    Editor = 7,
    Recorder = 8,
    Run = 9,
    Monitor = 10,
    Results = 11,
    Baselines = 12,
    Messages = 13,
    Preferences = 14,
    Keyboard = 15,
    Modes = 16,
    Troubleshooting = 17,
    About = 18,
}

internal static class WorkbenchHelpContext
{
    internal const ushort CommandBase = 0x7200;
    internal static ushort Command(HelpTopic topic) => (ushort)(CommandBase + (ushort)topic);
    internal static bool IsCommand(ushort command) => command > CommandBase && command <= CommandBase + (ushort)HelpTopic.About;
    // Resolve the deepest focused view first, then its functional owner. No caption matching.
    internal static HelpTopic Resolve(TView? view)
    {
        if (view is TGroup group && group.current is { } child) {
            var nested = Resolve(child); if (nested != HelpTopic.Index) return nested;
        }
        if (view?.helpCtx == WorkbenchScenariosWindow.Context) return HelpTopic.Scenarios;
        if (view?.helpCtx == WorkbenchRecordingReview.Context) return HelpTopic.Recorder;
        if (view?.helpCtx == WorkbenchScenarioEditorWindow.Context || view is WorkbenchScenarioMetadataDialog or WorkbenchStepKindDialog || view is WorkbenchScenarioStepDialog { CheckpointNoteField: not null }) return HelpTopic.Editor;
        if (view?.helpCtx == 0x7dc4) return HelpTopic.Properties;
        if (view?.helpCtx == NativeRecoveryDialog.Context) return HelpTopic.Start;
        if (view?.helpCtx == WorkbenchEnvironments.Context) return HelpTopic.Properties;
        if (view?.helpCtx == WorkbenchBaselineExplorer.Context) return HelpTopic.Baselines;
        if (view?.helpCtx == WorkbenchRunExplorer.Context) return HelpTopic.Run;
        if (view?.helpCtx == WorkbenchTestExplorer.Context) return HelpTopic.Workspace;
        return view switch {
            WorkbenchStartWindow => HelpTopic.Start,
            WorkbenchDialog or WorkbenchOverview => HelpTopic.Workspace,
            WorkbenchProjectInfoDialog or WorkbenchPropertiesDialog => HelpTopic.Properties,
            WorkbenchTargetDiagnosticsDialog or WorkbenchPrepareTargetDialog or WorkbenchPreparationProgressDialog or WorkbenchFixIssuesDialog => HelpTopic.Diagnostics,
            WorkbenchScenariosWindow => HelpTopic.Scenarios,
            WorkbenchScenarioEditorWindow or EditorDialog => HelpTopic.Editor,
            WorkbenchRecordingWindow => HelpTopic.Recorder,
            WorkbenchRunBasicDialog or WorkbenchRunDialog => HelpTopic.Run,
            WorkbenchRunMonitor => HelpTopic.Monitor,
            WorkbenchResultsDialog => HelpTopic.Results,
            WorkbenchComparisonWindow or WorkbenchCandidateDialog => HelpTopic.Baselines,
            WorkbenchMessagesWindow => HelpTopic.Messages,
            _ => HelpTopic.Index
        };
    }
}
