using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bazaar.Server.Garnet;

/// <summary>
/// A Windows Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. The host keeps the one job handle open for its whole life and
/// assigns each Garnet child to it, so when the host exits for any reason (even a crash or a kill) Windows closes the handle and kills
/// the children. This is the Windows counterpart of the parent-death signal the Valkey launcher uses on Linux.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObject : IDisposable
{
    private const int _extendedLimitInformationClass = 9;
    private const uint _killOnJobClose = 0x2000;

    private nint _job;

    /// <summary>Creates the job and sets the kill-on-close limit. Throws <see cref="Win32Exception"/> when Windows refuses.</summary>
    public WindowsJobObject()
    {
        _job = CreateJobObjectW(0, null);
        if (_job == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = _killOnJobClose } };
        if (!SetInformationJobObject(_job, _extendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>Adds the process to the job. Throws when Windows refuses, so the caller can fail closed.</summary>
    public void Assign(nint processHandle)
    {
        if (!AssignProcessToJobObject(_job, processHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, 0);
        if (job != 0)
        {
            CloseHandle(job);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
