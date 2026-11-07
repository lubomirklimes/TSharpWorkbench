using System.Text.Json;
using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

// Native HLP resource, contextual routing, navigation, failure handling and packaging.
// The real WorkbenchApp runs on NullDriver; keys and commands go through the shared event queue as a user's would.
// Optional logical captures (Captures): TSWHELP_CAPTURE=<directory>, TSWHELP_PHASE=before|after.
[Collection("NonParallel")]
public sealed class HelpTests
{
    private static readonly string Target = TestWorkbenchHostTests.SampleTarget;

    [Theory]
    [InlineData(120,36)] [InlineData(140,40)] [InlineData(160,50)]
    public async Task Baseline(int width, int height)
    {
        if (Environment.GetEnvironmentVariable("TSWHELP_PHASE") != "before") return;
        string root = Temp();
        try { await Drive(width,height,root,Path.Combine(root,"home"),async (app,bridge) => {
            await Wait(bridge,()=>app.Start);
            Press(Keys.kbAltH); await Until(bridge,()=>MenuOpen(app)); await Capture(bridge,"help-menu");
            Press(Keys.kbEsc); await Until(bridge,()=>!MenuOpen(app));
            foreach(var pair in new[] { (WorkbenchApp.Shortcuts,"Keyboard Reference"), (WorkbenchApp.TestModes,"Test Modes and Drivers"), (WorkbenchApp.About,"About TSharpWorkbench") }) {
                Send(pair.Item1); await Wait(bridge,()=>Modal(app,pair.Item2)); await Capture(bridge,pair.Item2.Replace(' ','-'));
                Press(Keys.kbEsc); await Until(bridge,()=>Modal(app,pair.Item2)==null);
            }
        },showStart:true); } finally { Delete(root); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    // The summary as one line (wrapped values joined, runs of spaces collapsed); without any spaces for path checks.
    [Theory]
    [InlineData(120,36)] [InlineData(140,40)] [InlineData(160,50)]
    public async Task NavigationAndContext(int width, int height)
    {
        string root = Temp();
        try { await Drive(width,height,root,Path.Combine(root,"home"),async (app,bridge) => {
            await Wait(bridge,()=>app.Start);
            Press(Keys.kbAltH); await Until(bridge,()=>MenuOpen(app)); await Capture(bridge,"help-menu");
            Press(Keys.kbEsc); await Until(bridge,()=>!MenuOpen(app));
            Press(Keys.kbF1); var help = await Wait(bridge,()=>app.HelpWindow);
            await Until(bridge,()=>string.Join(" ",Rows()).Contains("Open a recent project")); await Capture(bridge,"start-help");
            Press(Keys.kbF1); await Until(bridge,()=>string.Join(" ",Rows()).Contains("Choose a topic")); await Capture(bridge,"index");
            await bridge.Invoke(()=>Assert.Same(help,app.HelpWindow));
            Press(Keys.kbEnter); await Until(bridge,()=>string.Join(" ",Rows()).Contains("Open a recent project"));
            Press(Keys.kbBack); await Until(bridge,()=>string.Join(" ",Rows()).Contains("Choose a topic"));
            Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
            Send(WorkbenchHelpContext.Command(HelpTopic.Keyboard)); await Wait(bridge,()=>app.HelpWindow);
            await Capture(bridge,"keyboard-top"); Press(Keys.kbPgDn); await Capture(bridge,"keyboard-scrolled");
            await bridge.Invoke(()=>Assert.True(app.HelpWindow!.Viewer.delta.y > 0));
            Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
            await bridge.Invoke(()=>app.ActivateProject(new WorkbenchProject { Name="Help test", Target=Target, OutputRoot=Path.Combine(root,"runs") },Path.Combine(root,"help.twproj")));
            await Loaded(app,bridge,16);
            Press(Keys.kbF1); await Wait(bridge,()=>app.HelpWindow);
            await Until(bridge,()=>string.Join(" ",Rows()).Contains("Project Overview and Test Explorer")); await Capture(bridge,"workspace-f1");
            Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
            foreach(var entry in new[] { (Keys.kbF3,"Project Information"), (Keys.kbF4,"Project Properties") }) {
                Press(entry.Item1); var dialog=await Wait(bridge,()=>Modal(app,entry.Item2));
                Press(Keys.kbF1); await Wait(bridge,()=>app.HelpWindow);
                await Until(bridge,()=>string.Join(" ",Rows()).Contains("Project Properties and Environments"));
                await Capture(bridge,entry.Item2.Replace(' ','-')+"-f1");
                Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
                await bridge.Invoke(()=>Assert.Same(dialog,app.DeskTop!.TopView()));
                Press(Keys.kbEsc); await Until(bridge,()=>Modal(app,entry.Item2)==null);
            }
            Send(WorkbenchHelpContext.Command(HelpTopic.Modes)); await Wait(bridge,()=>app.HelpWindow); await Capture(bridge,"modes");
            Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
            Send(WorkbenchApp.MessagesWindow); await Wait(bridge,()=>app.Messages);
            Press(Keys.kbF1); await Wait(bridge,()=>app.HelpWindow);
            await Until(bridge,()=>string.Join(" ",Rows()).Contains("Alt+6 opens Messages")); await Capture(bridge,"messages-f1");
            Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
            Press(Keys.kbEsc); await Until(bridge,()=>app.Messages==null);
            Press(Keys.kbF5); var run=await Wait(bridge,()=>app.DeskTop!.TopView() is WorkbenchRunBasicDialog or WorkbenchRunDialog ? app.DeskTop.TopView() : null);
            Press(Keys.kbF1); await Wait(bridge,()=>app.HelpWindow);
            await Until(bridge,()=>string.Join(" ",Rows()).Contains("Configure Test Run")); await Capture(bridge,"run-f1");
            Press(Keys.kbEsc); await Until(bridge,()=>app.HelpWindow==null);
            await bridge.Invoke(()=>Assert.Same(run,app.DeskTop!.TopView()));
            Press(Keys.kbEsc); await Until(bridge,()=>app.DeskTop!.TopView()!=run);
            await bridge.Invoke(()=>app.HelpPath=Path.Combine(root,"missing.hlp"));
            Press(Keys.kbF1); var error=await Wait(bridge,()=>Modal(app,"Help unavailable"));
            await Capture(bridge,"missing-help"); Press(Keys.kbF1); await Settle(bridge);
            await bridge.Invoke(()=>Assert.Same(error,app.DeskTop!.TopView()));
            Press(Keys.kbEsc); await Until(bridge,()=>Modal(app,"Help unavailable")==null);
            Press(Keys.kbF3); await Wait(bridge,()=>Modal(app,"Project Information"));
            Press(Keys.kbEsc); await Until(bridge,()=>Modal(app,"Project Information")==null);
        },showStart:true); } finally { Delete(root); }
    }

    [Fact]
    public void ResourceAndAllLinksResolveAndDamagedFilesFail()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Help","TSharpWorkbench.hlp");
        using(var resource=new WorkbenchHelpResource(path)) {
            foreach(HelpTopic id in Enum.GetValues<HelpTopic>()) {
                Assert.True(resource.File.index.Position((int)id)>0);
                var topic=resource.File.GetTopic((int)id); Assert.NotNull(topic.paragraphs);
                foreach(var link in topic.crossRefs!) Assert.True(resource.File.index.Position(link.@ref)>0);
            }
            Assert.Equal(17,resource.File.GetTopic(1).GetNumCrossRefs());
            using var source=JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(),"src","TSharpWorkbench","Help","TSharpWorkbench.help.json")));
            foreach(var entry in source.RootElement.EnumerateArray()) {
                var topic=resource.File.GetTopic(entry.GetProperty("Id").GetInt32());
                var paragraphs=new List<string>();
                for(var p=topic.paragraphs;p is not null;p=p.next) paragraphs.Add(p.Text.TrimEnd('\n'));
                Assert.Equal(entry.GetProperty("Title").GetString(),paragraphs[0]);
                var expected=entry.GetProperty("Paragraphs").EnumerateArray().Select(p=>p.GetString()!.TrimEnd('\n')).ToArray();
                Assert.Equal(expected,paragraphs.Skip(2).Take(expected.Length*2).Where((_,i)=>i%2==0));
                Assert.Equal(entry.GetProperty("Links").EnumerateArray().Select(p=>p.GetInt32()),topic.crossRefs!.Select(r=>r.@ref));
            }
        }
        Assert.Equal(HelpTopic.Index,WorkbenchHelpContext.Resolve(null));
        string root=Temp(); Directory.CreateDirectory(root);
        try {
            string broken=Path.Combine(root,"bad.hlp");
            Assert.Throws<FileNotFoundException>(()=>new WorkbenchHelpResource(broken));
            File.WriteAllBytes(broken,[1,2,3]); Assert.Throws<InvalidDataException>(()=>new WorkbenchHelpResource(broken));
            var bytes=File.ReadAllBytes(path); bytes[20]^=1; File.WriteAllBytes(broken,bytes); File.Copy(path+".sha256",broken+".sha256");
            Assert.Throws<InvalidDataException>(()=>new WorkbenchHelpResource(broken));
            bytes[0]=0; File.WriteAllBytes(broken,bytes); File.WriteAllText(broken+".sha256",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            Assert.Throws<InvalidDataException>(()=>new WorkbenchHelpResource(broken));
        } finally { Delete(root); }
    }

    [Fact]
    public void MenuHasUniqueMnemonicsAndEveryTopicHasAHome()
    {
        using var driver=new DriverScope(); using var app=new WorkbenchApp("","",showStart:true);
        var help=Items(app.MenuBar!.Menu!.Items).Single(i=>i.Name=="~H~elp");
        var entries=Items(help.SubMenu!.Items).Where(i=>i.Name!=null).ToArray();
        Assert.Equal(WorkbenchHelpContext.Command(HelpTopic.Index),entries[0].Command);
        Assert.Equal(WorkbenchApp.About,entries[^1].Command);
        var mnemonics=entries.Select(i=>i.Name!.Split('~')[1].ToUpperInvariant()).ToArray();
        Assert.Equal(mnemonics.Length,mnemonics.Distinct().Count());
        foreach(HelpTopic topic in Enum.GetValues<HelpTopic>().Where(t=>t!=HelpTopic.About && t!=HelpTopic.Modes))
            Assert.Contains(entries,i=>i.Command==WorkbenchHelpContext.Command(topic));
        Assert.Contains(entries,i=>i.Command==WorkbenchApp.TestModes);
    }

    [Fact]
    public void PublishedResourceWorksOutsideCheckout()
    {
        if (Environment.GetEnvironmentVariable("TSWHELP_PUBLISH") is not { Length: > 0 } publish) return;
        string root=Temp(); Directory.CreateDirectory(root);
        try {
            string path=Path.Combine(publish,"Help","TSharpWorkbench.hlp");
            File.Copy(path,Path.Combine(root,"TSharpWorkbench.hlp"));
            File.Copy(path+".sha256",Path.Combine(root,"TSharpWorkbench.hlp.sha256"));
            using var help=new WorkbenchHelpResource(Path.Combine(root,"TSharpWorkbench.hlp"));
            Assert.Equal(17,help.File.GetTopic(1).GetNumCrossRefs());
            Assert.True(File.Exists(Path.Combine(publish,"LICENSE")));
            using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(publish,"TSharpWorkbench.exe"),"--help") {
                WorkingDirectory=root, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true
            })!;
            Assert.True(process.WaitForExit(15000)); Assert.Equal(0,process.ExitCode);
            Assert.Contains("TestWorkbench",process.StandardOutput.ReadToEnd());
        } finally { Delete(root); }
    }
    private static string NoSpace(WorkbenchProjectInfoDialog dialog) => Squeeze(string.Concat(dialog.Lines));
    private static string Squeeze(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");

    private static string SaveProject(string folder, string name, WorkbenchProject project)
    { Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); project.Save(path); return path; }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "uxc1-" + Guid.NewGuid().ToString("N")[..10]);
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TSharpWorkbench.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    private static IEnumerable<TMenuItem> Items(TMenuItem? first) { for (var i = first; i is not null; i = i.Next) yield return i; }
    private static bool MenuOpen(WorkbenchApp app) => Children(app).Any(v => v is TMenuBox) || app.current is TMenuView or TMenuBox;
    private static TDialog? Modal(WorkbenchApp app, string title) => app.DeskTop!.TopView() is TDialog d && d.title == title ? d : null;
    private static string[] Rows()
    {
        var s = ScreenSnapshot.CopyRoot();
        return Enumerable.Range(0, s.Height).Select(y => new string(Enumerable.Range(0, s.Width).Select(x => s[x, y].Character).ToArray())).ToArray();
    }
    private static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break; var first = group.last.Next!;
        for (TView? item = first; item is not null; item = item.Next) { yield return item; if (item.Next == first) break; }
    }
    private static IEnumerable<TView> Descendants(TGroup group)
    {
        foreach (var child in Children(group)) { yield return child; if (child is TGroup g) foreach (var d in Descendants(g)) yield return d; }
    }
    // Logical captures: the NullDriver cell screen of the real WorkbenchApp (diagnostic bitmap font; never a desktop screenshot).
    private static async Task Capture(Bridge bridge, string state)
    {
        await Settle(bridge);
        if (Environment.GetEnvironmentVariable("TSWHELP_CAPTURE") is not { Length: > 0 } root) return;
        string phase = Environment.GetEnvironmentVariable("TSWHELP_PHASE") is { Length: > 0 } p ? p : "before";
        await bridge.Invoke(() =>
        {
            Directory.CreateDirectory(root); var screen = ScreenSnapshot.CopyRoot();
            string name = $"{phase}-{state}-{screen.Width}x{screen.Height}";
            byte[] cells = screen.Serialize(), png = LogicalPng.Encode(screen);
            File.WriteAllBytes(Path.Combine(root, name + ".logical-nulldriver.png"), png);
            File.WriteAllLines(Path.Combine(root, name + ".logical-nulldriver.txt"),
                Enumerable.Range(0, screen.Height).Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen[x, y].Character).ToArray()).TrimEnd()));
            File.WriteAllText(Path.Combine(root, name + ".logical-nulldriver.json"), JsonSerializer.Serialize(new
            {
                name, phase, state, grid = $"{screen.Width}x{screen.Height}", captureType = "logical-nulldriver", driver = "NullDriver",
                test = nameof(HelpTests), renderer = LogicalPng.Renderer, cellsSha256 = ScreenSnapshot.Hash(cells), pngSha256 = ScreenSnapshot.Hash(png)
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    private static async Task Drive(int width, int height, string root, string home, Func<WorkbenchApp, Bridge, Task> body, bool showStart = false)
    {
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "runs"), previousHome = ProjectLocation.Home;
        ProjectLocation.Home = home;
        try
        {
            using var driver = new DriverScope((ushort)width, (ushort)height);
            using var app = new WorkbenchApp(showStart ? "" : Target, output, new(showStart ? "" : Target, output, null, "80x25") { StoragePath = Path.Combine(root, "settings.json") }, showStart: showStart);
            var bridge = new Bridge(); app.DeskTop!.Insert(bridge);
            Exception? failure = null;
            Task worker = Task.Run(async () =>
            {
                try
                {
                    await body(app, bridge);
                    await bridge.Invoke(() => { app.Project?.CloseApproved(); app.EndModal(Views.cmQuit); });
                }
                catch (Exception ex)
                {
                    failure = new Exception(ex.Message + "\nNotices:\n" + string.Join("\n", app.Notifications.History.Select(n => $"{n.Level}: {n.Message} | {n.Details}")), ex);
                    await bridge.Invoke(() =>
                    {
                        for (int i = 0; i < 4 && app.DeskTop!.TopView() is TDialog d; i++) d.EndModal(Views.cmCancel);
                        if (app.Project is { Busy: true }) Command(app.Project, WorkbenchDialog.CancelRun);
                        app.Project?.CloseApproved(); app.EndModal(Views.cmQuit);
                    }).WaitAsync(TimeSpan.FromSeconds(20));
                }
            });
            AppLifecycleGuard.Run(app);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(240)), "Worker did not stop.");
            Assert.Null(failure);
        }
        finally { ProjectLocation.Home = previousHome; }
    }
    private static async Task<WorkbenchDialog> Loaded(WorkbenchApp app, Bridge bridge, int count) =>
        await Wait(bridge, () => app.Project is { Busy: false } p && p.TestList.Strings.Count >= count ? p : null, 60);
    private static void Command(TView view, ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; view.HandleEvent(ref ev); }
    private static void Press(ushort key) { var ev = new TEvent { What = Events.evKeyDown }; ev.keyDown.keyCode = key; ev.keyDown.charScan = new CharScanType(key); TEventQueue.Enqueue(ev); }
    private static void Send(ushort command) { var ev = new TEvent { What = Events.evCommand }; ev.message.command = command; TEventQueue.Enqueue(ev); }
    private static async Task Settle(Bridge bridge) { await bridge.Invoke(() => { }); await Task.Delay(60); await bridge.Invoke(() => { }); }
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
