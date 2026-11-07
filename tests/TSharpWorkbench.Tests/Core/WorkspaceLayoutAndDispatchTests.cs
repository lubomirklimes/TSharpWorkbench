using TSharpVision.Constants;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Tests.Infrastructure;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

[Collection("NonParallel")]
public sealed class WorkspaceLayoutAndDispatchTests
{
    internal static IEnumerable<TView> Children(TGroup group)
    {
        if (group.last is null) yield break;
        TView first = group.last.Next!;
        for (TView? view = first; view is not null; view = view.Next)
        { yield return view; if (view.Next == first) break; }
    }
    // Rewritten for the workspace (the former 80x25 geometry and "Test details" label no longer exist).
    // A host/desktop resize relayouts the Workspace across the supported Workbench grids and back without overlap.
    [Theory]
    [InlineData(140,40,120,36)]
    [InlineData(120,36,160,50)]
    [InlineData(160,50,140,40)]
    public void WorkspaceFillsDesktopAndResizesWithoutOverlap(int width, int height, int otherWidth, int otherHeight)
    {
        using var driver = new DriverScope((ushort)width,(ushort)height);
        using var app = new TApplication();
        using var main = new WorkbenchDialog(app.DeskTop!, "", "");
        app.DeskTop!.Insert(main);
        Assert.Equal(app.DeskTop.size, main.size);
        Assert.Equal(new TPoint(0,0), main.origin);
        Verify(main);
        app.DeskTop.ChangeBounds(new(0,1,otherWidth,otherHeight-1));
        Assert.Equal(app.DeskTop.size, main.size);
        Verify(main);
        app.DeskTop.ChangeBounds(new(0,1,width,height-1));
        Assert.Equal(app.DeskTop.size, main.size);
        Verify(main);
        static void Verify(WorkbenchDialog main)
        {
            var controls = Children(main).Where(v => v is not TFrame).ToArray();
            foreach (var v in controls)
            {
                Assert.True(v.origin.x >= 1 && v.origin.y >= 1, $"{v.GetType().Name} outside at {main.size.x}x{main.size.y}");
                Assert.True(v.size.x > 0 && v.size.y > 0);
                Assert.True(v.origin.x + v.size.x <= main.size.x - 1 && v.origin.y + v.size.y <= main.size.y - 1, $"{v.GetType().Name} outside at {main.size.x}x{main.size.y}");
            }
            // Scenarios above Details in the right column; both keep usable heights at every supported grid.
            var list = main.TestList; var details = main.DetailPane;
            Assert.Equal(list.origin.x, details.origin.x);
            Assert.True(details.origin.y > list.origin.y + list.size.y - 1);
            Assert.True(list.size.y >= 10 && details.size.y >= 12, $"list {list.size.y}, details {details.size.y} at {main.size.x}x{main.size.y}");
            var selectable = controls.Where(v => (v.options & Views.ofSelectable) != 0).ToArray();
            for (int i=0;i<selectable.Length;i++) for(int j=i+1;j<selectable.Length;j++)
            {
                var a=selectable[i]; var b=selectable[j];
                Assert.False(a.origin.x < b.origin.x+b.size.x && b.origin.x < a.origin.x+a.size.x && a.origin.y < b.origin.y+b.size.y && b.origin.y < a.origin.y+a.size.y,
                    $"Overlapping {a.GetType().Name} and {b.GetType().Name}");
            }
        }
    }
    [Theory]
    [InlineData("")]
    [InlineData("missing.exe")]
    [InlineData("bad\0path")]
    public void F2DispatchRecoversFromInvalidTargetAndPreservesSelection(string path)
    {
        using var driver = new DriverScope();
        using var app = new TApplication();
        using var main = new WorkbenchDialog(app.DeskTop!,path,"");
        app.DeskTop!.Insert(main);
        var list=Children(main).OfType<ScenarioList>().Single(); list.LoadCatalog([new("one", "one", "description", "group", ScenarioGrid.Standard, [new(ScenarioStep.Checkpoint, "initial")])]); list.Toggle("one");
        using var status = WorkbenchApp.CreateStatusLine(app.GetExtent());
        var workspaceStatus = status.Defs;
        while (workspaceStatus is not null && workspaceStatus.Min != WorkbenchDialog.Context)
            workspaceStatus = workspaceStatus.Next;
        status.Items = workspaceStatus!.Items;
        var ev = new TEvent { What=Events.evKeyDown };
        ev.keyDown.keyCode=Keys.kbF2; ev.keyDown.charScan=new CharScanType(Keys.kbF2);
        status.HandleEvent(ref ev);
        Assert.Equal(Events.evCommand,ev.What); Assert.Equal(WorkbenchDialog.Load,ev.message.command);
        main.HandleEvent(ref ev);
        // The header ("Target ...") replaced the former "Target:" status texts; the reason goes to the
        // notification channel. The marks survive the failed refresh.
        Assert.Contains("Target", main.HeaderText);
        Assert.Equal(new[] { "one" },list.MarkedIds);
    }
    [Fact]
    public void ExistingSelectorsResolveUnicodeAndSpacesWithoutChangingWorkingDirectory()
    {
        string root=Path.Combine(Path.GetTempPath(),"gallery3a žluťoučký 状態 "+Guid.NewGuid().ToString("N"));
        string cwd=Environment.CurrentDirectory;
        Directory.CreateDirectory(root);
        string exe=Path.Combine(root,"target 状態.exe"); File.WriteAllText(exe,"");
        try
        {
            using var driver=new DriverScope();
            using var picker=new TFileDialog("*.exe","Target","Name",FileDialogOptions.fdOpenButton|FileDialogOptions.fdNoLoadDir,0);
            picker.SetData(Path.Combine(root,"*.exe")); picker.SetData(exe);
            picker.GetFileName(out string selected); Assert.Equal(exe,selected);
            using var directory=new WorkbenchDirectoryDialog(root);
            Assert.True(directory.Valid(Views.cmOK)); Assert.Equal(root,directory.SelectedPath);
            Assert.Equal(cwd,Environment.CurrentDirectory);
            Assert.Equal(root,WorkbenchDialog.InitialDirectory(exe));
            Assert.Equal(root,WorkbenchDialog.InitialDirectory(Path.Combine(root,"missing","child")));
        }
        finally { Directory.Delete(root,true); }
    }
}
