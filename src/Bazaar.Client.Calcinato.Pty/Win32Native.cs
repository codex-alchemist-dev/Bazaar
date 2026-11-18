using System.Runtime.InteropServices;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// kernel32 declarations for ConPTY. This file and <see cref="ConPtyBackend"/> are the only places Windows APIs appear
/// (doc 52, section 3). Nothing outside this project calls them.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static unsafe partial class Win32Native
{
    internal const uint ProcThreadAttributePseudoConsole = 0x00020016;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateSuspended = 0x00000004;
    internal const uint StartfUseStdHandles = 0x00000100;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint WaitObject0 = 0;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorBrokenPipe = 109;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CreatePipe(out nint readPipe, out nint writePipe, nint attributes, uint size);

    /// <summary>Returns an HRESULT.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint pseudoConsole);

    /// <summary>Returns an HRESULT.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial void ResizePseudoConsole(nint pseudoConsole, Coord size);

    [LibraryImport("kernel32.dll")]
    internal static partial void ClosePseudoConsole(nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int InitializeProcThreadAttributeList(nint list, int count, int flags, nuint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void SetConsoleCtrlHandler(nint handler, int add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CreateProcessW(
        nint applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        int inheritHandles,
        uint creationFlags,
        nint environment,
        nint currentDirectory,
        StartupInfoEx* startupInfo,
        ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int SetInformationJobObject(nint job, int infoClass, JobObjectExtendedLimitInformation* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetExitCodeProcess(nint process, uint* exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ReadFile(nint file, byte* buffer, uint count, uint* read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int WriteFile(nint file, byte* buffer, uint count, uint* written, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void CloseHandle(nint handle);
}

/// <summary>COORD.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Coord
{
    public short X;
    public short Y;
}

/// <summary>STARTUPINFOEXW, flattened so the layout is explicit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    public uint Cb;
    public nint Reserved;
    public nint Desktop;
    public nint Title;
    public uint X;
    public uint Y;
    public uint XSize;
    public uint YSize;
    public uint XCountChars;
    public uint YCountChars;
    public uint FillAttribute;
    public uint Flags;
    public ushort ShowWindow;
    public ushort Reserved2Size;
    public nint Reserved2;
    public nint StdInput;
    public nint StdOutput;
    public nint StdError;
    public nint AttributeList;
}

/// <summary>PROCESS_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public uint ProcessId;
    public uint ThreadId;
}

/// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
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

/// <summary>IO_COUNTERS.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}
