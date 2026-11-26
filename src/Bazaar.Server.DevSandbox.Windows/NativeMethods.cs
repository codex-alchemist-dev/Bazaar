using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Bazaar.Server.DevSandbox.Windows;

#pragma warning disable SA1307, CA1815, CS0649

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public int Length;
    public nint SecurityDescriptor;
    public int InheritHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoExW
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

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public uint ProcessId;
    public uint ThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityCapabilities
{
    public nint AppContainerSid;
    public nint Capabilities;
    public uint CapabilityCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobBasicLimitInformation
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
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobExtendedLimitInformation
{
    public JobBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobBasicUiRestrictions
{
    public uint UiRestrictionsClass;
}

#pragma warning restore SA1307, CA1815, CS0649

/// <summary>Source-generated P/Invoke declarations (no runtime marshalling stubs, so they are safe for trimming and AOT). Libraries load from System32 only.</summary>
internal static partial class NativeMethods
{
    internal const string Kernel32 = "kernel32.dll";
    internal const string Advapi32 = "advapi32.dll";
    internal const string Userenv = "userenv.dll";

    // Job limit flags.
    internal const uint JobLimitJobTime = 0x00000004;
    internal const uint JobLimitActiveProcess = 0x00000008;
    internal const uint JobLimitProcessMemory = 0x00000100;
    internal const uint JobLimitJobMemory = 0x00000200;
    internal const uint JobLimitDieOnUnhandledException = 0x00000400;
    internal const uint JobLimitBreakawayOk = 0x00000800;
    internal const uint JobLimitSilentBreakawayOk = 0x00001000;
    internal const uint JobLimitKillOnJobClose = 0x00002000;

    // Job information classes.
    internal const int JobObjectBasicUiRestrictions = 4;
    internal const int JobObjectBasicProcessIdList = 3;
    internal const int JobObjectExtendedLimitInformation = 9;

    // Every JOB_OBJECT_UILIMIT_* flag: HANDLES, READCLIPBOARD, WRITECLIPBOARD, SYSTEMPARAMETERS, DISPLAYSETTINGS, GLOBALATOMS, DESKTOP, EXITWINDOWS.
    internal const uint JobUiLimitAll = 0x000000FF;

    // CreateProcess flags.
    internal const uint CreateSuspended = 0x00000004;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint StartfUseStdHandles = 0x00000100;

    internal const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    internal const nuint ProcThreadAttributeHandleList = 0x00020002;

    internal const uint HandleFlagInherit = 0x00000001;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 0x102;
    internal const uint GenericRead = 0x80000000;
    internal const uint FileShareReadWrite = 3;
    internal const uint OpenExisting = 3;
    internal const uint DisableMaxPrivilege = 0x1;
    internal const uint TokenAssignPrimary = 0x1;
    internal const uint TokenDuplicate = 0x2;
    internal const uint TokenQuery = 0x8;
    internal const int HResultAlreadyExists = unchecked((int)0x800700B7);
    internal const int HResultNotFound = unchecked((int)0x80070490);
    internal const uint DaclSecurityInformation = 0x4;
    internal const uint ProtectedDaclSecurityInformation = 0x80000000;
    internal const int SeFileObject = 1;

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, nint information, uint length);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(SafeFileHandle job, int informationClass, nint information, uint length, out uint returnLength);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string? applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        nint startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport(Advapi32, EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUser(
        SafeFileHandle token,
        string? applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        nint startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial uint ResumeThread(SafeFileHandle thread);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint access, uint shareMode, ref SecurityAttributes attributes, uint disposition, uint flags, nint template);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(nint attributeList, uint attributeCount, uint flags, ref nuint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute, nint value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport(Kernel32)]
    internal static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport(Kernel32)]
    internal static partial nint LocalFree(nint memory);

    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentProcess();

    [LibraryImport(Advapi32, EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSidToSid(string stringSid, out nint sid);

    [LibraryImport(Advapi32, EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [LibraryImport(Advapi32, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint securityDescriptor, nint size);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSecurityDescriptorDacl(nint securityDescriptor, [MarshalAs(UnmanagedType.Bool)] out bool daclPresent, out nint dacl, [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [LibraryImport(Advapi32, EntryPoint = "SetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint SetNamedSecurityInfo(string objectName, int objectType, uint securityInformation, nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport(Advapi32)]
    internal static partial nint FreeSid(nint sid);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out SafeFileHandle token);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateRestrictedToken(
        SafeFileHandle existingToken,
        uint flags,
        uint disableSidCount,
        nint sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out SafeFileHandle newToken);

    [LibraryImport(Userenv, EntryPoint = "CreateAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint capabilityCount, out nint sid);

    [LibraryImport(Userenv, EntryPoint = "DeleteAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DeleteAppContainerProfile(string name);
}
