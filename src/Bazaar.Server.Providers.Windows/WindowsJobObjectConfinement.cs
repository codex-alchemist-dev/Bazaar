using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bazaar.Server.Portability.Sandbox;

namespace Bazaar.Server.Providers.Windows;

/// <summary>
/// Windows confinement of the portability parser child (doc 44 section 8.3, doc 03 provider rule): the child is put in
/// a job object with a per-process memory limit, a per-process user-CPU-time limit, an active-process limit of one (so
/// it cannot start another process) and kill-on-close. Killing the tree is <c>TerminateJobObject</c>.
/// The child is assigned to the job right after it starts, because .NET cannot start a process suspended; the first
/// instructions of the host run before the assignment, and the parser code runs well after it.
/// Not applied: network isolation and filesystem scoping (that needs an AppContainer or a firewall rule; reported in
/// <see cref="ConfinementPlan.NotApplied"/>). This provider has not been run on a Windows host yet.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObjectConfinement : IParserConfinement
{
    private const int _extendedLimitInformation = 9;
    private const uint _limitProcessTime = 0x2;
    private const uint _limitActiveProcess = 0x8;
    private const uint _limitProcessMemory = 0x100;
    private const uint _limitKillOnJobClose = 0x2000;

    public string Name => "windows-job-object";

    public ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(command);

        var applied = new List<string> { "process-tree-kill", "cpu-limit", "memory-limit-native" };
        var notApplied = new List<string> { "file-read-scope" };
        if (limits.NoChildProcesses) applied.Add("no-child-processes");
        if (limits.NoNetwork) notApplied.Add("no-network");
        if (limits.NoFileWrites) notApplied.Add("no-file-writes");

        return new ConfinementPlan(command, applied, notApplied)
        {
            AttachAfterStart = process => JobGuard.Attach(process, limits),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class JobGuard : IProcessGuard
    {
        private IntPtr _job;

        private JobGuard(IntPtr job)
        {
            _job = job;
        }

        public static JobGuard Attach(Process process, ConfinementLimits limits)
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            var info = new ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = _limitKillOnJobClose | _limitProcessMemory | _limitProcessTime;
            info.BasicLimitInformation.PerProcessUserTimeLimit = limits.CpuSeconds * 10_000_000L;
            info.ProcessMemoryLimit = checked((UIntPtr)(ulong)limits.MemoryBytes);
            if (limits.NoChildProcesses)
            {
                info.BasicLimitInformation.LimitFlags |= _limitActiveProcess;
                info.BasicLimitInformation.ActiveProcessLimit = 1;
            }

            if (!SetInformationJobObject(job, _extendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>())
                || !AssignProcessToJobObject(job, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new Win32Exception(error);
            }

            return new JobGuard(job);
        }

        public void KillTree()
        {
            var job = _job;
            if (job != IntPtr.Zero) _ = TerminateJobObject(job, 1);
        }

        public void Dispose()
        {
            var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero) _ = CloseHandle(job);
        }
    }
}
