using System.Text.Json;
using TSharpVision;
using TSharpVision.Drivers.Console;
using TSharpWorkbench.Testing;
using TSharpWorkbench.Proxy;

/// <summary>Native renderer probe in a disposable owned console; not an application automation path.</summary>
internal static class RenderingProbe
{
    internal static int Run(string marker)
    {
        ProxyConsole.Resize(80,25);
        var before=ProxyConsole.Inspect();
        using var proxy=WorkbenchProxyDriver.Wrap(new Win32ConsoleDriver(),"Win32ConsoleDriver");
        TEventQueue.Resume(); TEventQueue.ClaimUiThread();
        proxy.Initialize(); TDisplay.driver=proxy;
        using var app=new TApplication();
        app.DrawView();
        var captures=new List<object>();
        void Check(bool ok,string reason) {if(!ok)throw new InvalidOperationException(reason);}
        void Capture(string name,bool expectShadow=true)
        {
            var cp=proxy.CaptureCheckpoint(app);
            Check(cp.WritesMatchRoot==expectShadow,name+" shadow");
            Check(ProxyConsole.Matches(cp.Root),name+" native cell readback");
            captures.Add(new{name,cp.Revision,cp.WritesMatchRoot,nativeMatchesRoot=true});
        }
        proxy.CaptureCheckpoint(app);
        Capture("full");
        void Write(int x,int y,int w,int h,TScreenChar[] data)
        {
            proxy.WriteBuf(x,y,w,h,data);
            for(int row=0;row<h;row++)for(int col=0;col<w;col++)
                TScreen.ScreenBuffer!.SetChar((uint)(x+col),(uint)(y+row),data[row*w+col]);
        }
        Write(3,4,3,2,Enumerable.Repeat(new TScreenChar('▒',0x1e),6).ToArray());
        Capture("fragments");
        Write(4,4,2,1,[new('╔',0x2f),new('═',0x3e)]);
        Capture("overlap");
        proxy.SetCursorType(0x0607);proxy.SetCaretPosition(7,6);
        var cursor=ProxyConsole.Inspect();
        Check(cursor.CursorVisible && cursor.CursorX==7 && cursor.CursorY==6 && cursor.CursorSize==100,"cursor position/shape");
        proxy.SetCursorType(0);
        Check(!ProxyConsole.Inspect().CursorVisible,"hidden cursor");
        proxy.ClearScreen(80,25);
        Check(!proxy.CaptureCheckpoint(app).WritesMatchRoot,"clear invalidates shadow");
        app.Redraw(); Capture("redraw");
        app.ShutDown(); proxy.Shutdown(); proxy.Dispose();
        var after=ProxyConsole.Inspect();
        Check(before.InputMode==after.InputMode && before.OutputMode==after.OutputMode,"restored modes");
        File.WriteAllText(marker,JsonSerializer.Serialize(new{allPassed=true,before,after,cursor,captures}));
        return 0;
    }
}

