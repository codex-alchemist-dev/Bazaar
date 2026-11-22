using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bazaar.Server.Providers.ClaudeCode;

namespace Bazaar.Server.Providers.ClaudeCode.Windows;

/// <summary>
/// One Windows Job Object with kill-on-close for every Claude Code child (docs/design/91 section 6). When the server process
/// ends, for any reason, Windows closes the job handle and ends the children and their descendants. The child is assigned just
/// after it starts, so a grandchild started in that first instant could escape; the process runner also kills the tree on
/// cancel and timeout, which covers the normal paths.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsJobObject : IChildProcessContainer, IDisposable
{
    private const int _killOnJobClose = 0x2000;
    private const int _extendedLimitInformation = 9;
    private readonly nint _job;

    public WindowsJobObject()
    {
        _job = CreateJobObjectW(0, null);
        if (_job == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var info = default(ExtendedLimits);
        info.Basic.LimitFlags = _killOnJobClose;
        if (!SetInformationJobObject(_job, _extendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(_job);
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    public void Adopt(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_job, process.Handle))
        {
            // A refusal is not fatal: cancel and timeout still kill the tree. Surface it through the exception only on a bad handle.
            var error = Marshal.GetLastPInvokeError();
            if (!process.HasExited)
            {
                throw new Win32Exception(error, "AssignProcessToJobObject failed.");
            }
        }
    }

    public void Dispose() => CloseHandle(_job);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
