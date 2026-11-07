using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TSharpWorkbench.Testing;

/// <summary>Owns one new-console process and all its descendants. No inherited handles or breakaway.
/// The unnamed, non-inheritable kill-on-close job also handles controller death. Assignment happens
/// while suspended: failure terminates only the process handle returned by CreateProcess.</summary>
internal sealed class OwnedConsoleProcess : IDisposable
{
    private SafeFileHandle? job;
    internal Process Process { get; private set; } = null!;
    internal int ProcessId { get; private set; }
    internal long ConsoleWindow { get; set; }

    internal async Task WaitForWindowReleaseAsync()
    {
        using var deadline = new CancellationTokenSource(5000);
        while (ConsoleWindow != 0 && IsWindow((IntPtr)ConsoleWindow))
            await Task.Delay(20, deadline.Token);
    }
    internal static OwnedConsoleProcess Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned Console requires Windows.");
        if (start.Arguments.Length != 0)
            throw new ArgumentException("Owned Console requires ArgumentList; raw command lines are unsupported.");
        var owner = new OwnedConsoleProcess();
        var pi = new ProcessInformation();
        try
        {
            owner.job = CreateJobObjectW(IntPtr.Zero, null);
            if (owner.job.IsInvalid) throw Error("CreateJobObject");
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway.
            if (!SetInformationJobObject(owner.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw Error("SetInformationJobObject");
            string environment = string.Join('\0', start.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + "=" + p.Value)) + "\0\0";
            var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            // CREATE_NEW_CONSOLE | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT.
            // STARTF_USESTDHANDLES absent; Windows supplies the child's NEW console handles.
            if (!CreateProcessW(start.FileName, new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(QuoteArgument))),
                IntPtr.Zero, IntPtr.Zero, false, 0x414, environment, string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory, ref si, out pi))
                throw Error("CreateProcessW owned console");
            owner.ProcessId = (int)pi.ProcessId;
            if (!AssignProcessToJobObject(owner.job, pi.Process)) throw Error("AssignProcessToJobObject");
            // Open/retain the managed process handle while the native creation handle still pins identity.
            owner.Process = Process.GetProcessById(owner.ProcessId);
            _ = owner.Process.Handle;
            if (ResumeThread(pi.Thread) == uint.MaxValue) throw Error("ResumeThread");
            return owner;
        }
        catch
        {
            if (pi.Process != IntPtr.Zero)
            {
                TerminateProcess(pi.Process, 125);
                WaitForSingleObject(pi.Process, 5000);
            }
            owner.Process?.Dispose();
            owner.Dispose();
            throw;
        }
        finally
        {
            if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
            if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
        }
    }

    // Windows CRT parsing: backslashes before quotes and the closing quote must be doubled.
    internal static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder().Append('"');
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
    internal void Terminate() => Dispose();
    public void Dispose() { job?.Dispose(); job = null; }
    private static Win32Exception Error(string operation)
    {
        int code=Marshal.GetLastWin32Error();
        return new(code,$"{operation}: {new Win32Exception(code).Message} (Win32 {code}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, string environment, string directory,
        ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}
