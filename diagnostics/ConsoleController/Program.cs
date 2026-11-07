using System.Runtime.InteropServices;
using System.Text.Json;
using TSharpWorkbench.Testing;

if (!OperatingSystem.IsWindows()) return 3;
string target=Environment.GetEnvironmentVariable("TSW_CONSOLE_TARGET") ?? args[0];
string marker=Environment.GetEnvironmentVariable("TSW_CONSOLE_MARKER") ?? args[1];
if(Environment.GetEnvironmentVariable("TSW_CONSOLE_RENDER_PROBE")=="1")
{
    try { return RenderingProbe.Run(marker); }
    catch(Exception ex) {File.WriteAllText(marker,JsonSerializer.Serialize(new{allPassed=false,error=ex.ToString()}));return 9;}
}
bool parentProbe=Environment.GetEnvironmentVariable("TSW_CONSOLE_PARENT_PROBE")=="1";
string Facts()
{
    var info=new BufferInfo {Size=(uint)Marshal.SizeOf<BufferInfo>(),Colors=new uint[16]};
    if(!GetConsoleScreenBufferInfoEx(GetStdHandle(-11),ref info)) throw new InvalidOperationException("Parent buffer facts unavailable.");
    return JsonSerializer.Serialize(new{native=ProxyConsole.Inspect(),info.Colors,title=Console.Title,inputCP=GetConsoleCP(),outputCP=GetConsoleOutputCP()});
}
string PeekInput()
{
    if(!GetNumberOfConsoleInputEvents(GetStdHandle(-10),out uint count) || count>4096)
        throw new InvalidOperationException("Parent input count unavailable or exceeds probe bound.");
    if(count==0) return "";
    var bytes=new byte[count*20]; // Win32 INPUT_RECORD: ushort type, padding, 16-byte union.
    if(!PeekConsoleInputW(GetStdHandle(-10),bytes,count,out uint read)) throw new InvalidOperationException("Parent input peek failed.");
    return Convert.ToHexString(bytes.AsSpan(0,(int)read*20));
}
if(parentProbe) ProxyConsole.Resize(80,25);
string? before=parentProbe?Facts():null;
string? inputBefore=parentProbe?PeekInput():null;
await using var session = new TargetSession(target, null, "80x25", default, 5000, 30, "Win32ConsoleDriver", true);
var hello = await session.ConnectAsync();
if(parentProbe)
{
    await session.SendAsync(Protocol.Operations.Start,Protocol.States.Started);
    await session.SendAsync(Protocol.Operations.Input,Protocol.States.Routed,input:new("resize","140x40"));
    await session.SendAsync(Protocol.Operations.Checkpoint,Protocol.States.Captured,"resized");
    await session.SendAsync(Protocol.Operations.Shutdown,Protocol.States.ShutdownAccepted);
    await session.WaitForExitAsync(); await session.DisposeAsync();
    string after=Facts();
    string inputAfter=PeekInput();
    bool inputPrefixPreserved=inputAfter.StartsWith(inputBefore!,StringComparison.Ordinal);
    File.WriteAllText(marker,JsonSerializer.Serialize(new{before,after,inputBefore,inputAfter,inputPrefixPreserved,child=hello.Target!.Console,session.Cleanup}));
    return before==after && inputPrefixPreserved && session.Cleanup ? 0 : 7;
}
// Parent test terminates this exact process; job closure must release target and console.
File.WriteAllText(marker, JsonSerializer.Serialize(hello.Target!.Console));
await Task.Delay(Timeout.Infinite);
return 0;

[StructLayout(LayoutKind.Sequential)]
struct Coord {public short X,Y;}
[StructLayout(LayoutKind.Sequential)]
struct Rect {public short Left,Top,Right,Bottom;}
[StructLayout(LayoutKind.Sequential)]
struct BufferInfo
{
    public uint Size;public Coord Dimensions,Cursor;public ushort Attributes;public Rect Window;
    public Coord Maximum;public ushort PopupAttributes;public int Fullscreen;
    [MarshalAs(UnmanagedType.ByValArray,SizeConst=16)]public uint[] Colors;
}
partial class Program
{
    [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] internal static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] internal static extern bool GetNumberOfConsoleInputEvents(IntPtr input,out uint count);
    [DllImport("kernel32.dll")] internal static extern bool PeekConsoleInputW(IntPtr input,[Out] byte[] records,uint length,out uint read);
    [DllImport("kernel32.dll")] internal static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] internal static extern bool GetConsoleScreenBufferInfoEx(IntPtr output,ref BufferInfo info);
}
