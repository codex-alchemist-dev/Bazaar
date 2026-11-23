using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>
/// The real Win32 implementation of <see cref="IWindowsConfinementApi"/>: kernel32 (job objects, process creation, pipes), advapi32
/// (SIDs, security descriptors, restricted tokens) and userenv (AppContainer profiles), through source-generated P/Invoke.
/// Windows only. It is exercised on Windows by the real-API tests and the provider's self-test; it is not run on any other host.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32ConfinementApi : IWindowsConfinementApi
{
    private const string _modifyMask = "0x1301bf"; // FILE_GENERIC_READ | FILE_GENERIC_WRITE | FILE_GENERIC_EXECUTE | DELETE

    public bool IsWindows => true;

    public bool IsAppContainerSupported() => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public bool FileExists(string path) => File.Exists(path);

    public IWindowsJob CreateJob(WindowsJobSpec spec)
    {
        var job = NativeMethods.CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
        }

        try
        {
            var limits = new JobExtendedLimitInformation
            {
                ProcessMemoryLimit = checked((nuint)spec.ProcessMemoryBytes),
                JobMemoryLimit = checked((nuint)spec.JobMemoryBytes),
            };
            limits.BasicLimitInformation.LimitFlags =
                NativeMethods.JobLimitKillOnJobClose
                | NativeMethods.JobLimitActiveProcess
                | NativeMethods.JobLimitProcessMemory
                | NativeMethods.JobLimitJobMemory
                | NativeMethods.JobLimitJobTime
                | NativeMethods.JobLimitDieOnUnhandledException;
            limits.BasicLimitInformation.ActiveProcessLimit = checked((uint)spec.ActiveProcessLimit);
            limits.BasicLimitInformation.PerJobUserTimeLimit = spec.CpuTime.Ticks; // 100 ns units, like TimeSpan ticks
            SetInformation(job, NativeMethods.JobObjectExtendedLimitInformation, limits);

            // No desktop switch, no clipboard, no global atoms, no handles to USER objects outside the job, no system parameter or display changes, no logoff.
            SetInformation(job, NativeMethods.JobObjectBasicUiRestrictions, new JobBasicUiRestrictions { UiRestrictionsClass = NativeMethods.JobUiLimitAll });
            return new Win32Job(job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public WindowsAppContainerProfile CreateAppContainerProfile(string name)
    {
        var hr = NativeMethods.CreateAppContainerProfile(name, "Bazaar sandbox run", "Confinement profile for one Bazaar dev-sandbox run. No capabilities.", 0, 0, out var sid);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return new WindowsAppContainerProfile(name, SidToString(sid));
        }
        finally
        {
            NativeMethods.FreeSid(sid);
        }
    }

    public void DeleteAppContainerProfile(string name)
    {
        var hr = NativeMethods.DeleteAppContainerProfile(name);
        if (hr != 0 && hr != NativeMethods.HResultNotFound)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void GrantWorkDirectoryAccess(string directory, string? appContainerSid)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no SID.");
        // P = protected (no inherited entries), AI = auto-inherited flag; OI/CI = inherited by files and sub-directories.
        // FA = full control for the current user (cleanup) and SYSTEM; the AppContainer gets modify, nothing else gets anything.
        var sddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;" + user.Value + ")"
            + (appContainerSid is null ? string.Empty : "(A;OICI;" + _modifyMask + ";;;" + appContainerSid + ")");
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertStringSecurityDescriptorToSecurityDescriptor failed");
        }

        try
        {
            if (!NativeMethods.GetSecurityDescriptorDacl(descriptor, out var present, out var dacl, out _) || !present)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The security descriptor has no DACL");
            }

            var error = NativeMethods.SetNamedSecurityInfo(
                directory,
                NativeMethods.SeFileObject,
                NativeMethods.DaclSecurityInformation | NativeMethods.ProtectedDaclSecurityInformation,
                0,
                0,
                dacl,
                0);
            if (error != 0)
            {
                throw new Win32Exception(checked((int)error), "SetNamedSecurityInfo failed");
            }
        }
        finally
        {
            NativeMethods.LocalFree(descriptor);
        }
    }

    public IWindowsProcess CreateProcessSuspended(WindowsProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SafeFileHandle? nul = null, outRead = null, outWrite = null, errRead = null, errWrite = null, token = null, restricted = null;
        nint attributeList = 0, sid = 0, capabilities = 0, handleList = 0, commandLine = 0, environment = 0, startupInfo = 0;
        var attributeListReady = false;
        var succeeded = false;
        try
        {
            var inheritable = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };

            // Standard input is the NUL device: closed for all practical purposes.
            nul = NativeMethods.CreateFile("NUL", NativeMethods.GenericRead, NativeMethods.FileShareReadWrite, ref inheritable, NativeMethods.OpenExisting, 0, 0);
            if (nul.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Opening NUL failed");
            }

            CreateInheritablePipe(out outRead, out outWrite, ref inheritable);
            CreateInheritablePipe(out errRead, out errWrite, ref inheritable);

            var attributeCount = request.Mode == WindowsConfinementMode.AppContainer ? 2u : 1u;
            nuint size = 0;
            NativeMethods.InitializeProcThreadAttributeList(0, attributeCount, 0, ref size); // fails with ERROR_INSUFFICIENT_BUFFER and reports the size
            if (size == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList could not report a size");
            }

            attributeList = Marshal.AllocHGlobal((nint)size);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");
            }

            attributeListReady = true;

            // Only these three handles are inherited by the child.
            handleList = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handleList, 0, nul.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, nint.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 2 * nint.Size, errWrite.DangerousGetHandle());
            UpdateAttribute(attributeList, NativeMethods.ProcThreadAttributeHandleList, handleList, (nuint)(3 * nint.Size));

            if (request.Mode == WindowsConfinementMode.AppContainer)
            {
                if (request.AppContainerSid is null)
                {
                    throw new InvalidOperationException("AppContainer mode needs the profile SID.");
                }

                if (!NativeMethods.ConvertStringSidToSid(request.AppContainerSid, out sid))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertStringSidToSid failed");
                }

                // No capabilities: no network, no file access outside what the SID itself was granted.
                var securityCapabilities = new SecurityCapabilities { AppContainerSid = sid, Capabilities = 0, CapabilityCount = 0 };
                capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
                Marshal.StructureToPtr(securityCapabilities, capabilities, fDeleteOld: false);
                UpdateAttribute(attributeList, NativeMethods.ProcThreadAttributeSecurityCapabilities, capabilities, (nuint)Marshal.SizeOf<SecurityCapabilities>());
            }
            else
            {
                token = OpenOwnToken();
                if (!NativeMethods.CreateRestrictedToken(token, NativeMethods.DisableMaxPrivilege, 0, 0, 0, 0, 0, 0, out restricted))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateRestrictedToken failed");
                }
            }

            var info = new StartupInfoExW
            {
                Cb = (uint)Marshal.SizeOf<StartupInfoExW>(),
                Flags = NativeMethods.StartfUseStdHandles,
                StdInput = nul.DangerousGetHandle(),
                StdOutput = outWrite.DangerousGetHandle(),
                StdError = errWrite.DangerousGetHandle(),
                AttributeList = attributeList,
            };
            startupInfo = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoExW>());
            Marshal.StructureToPtr(info, startupInfo, fDeleteOld: false);

            commandLine = AllocateUnicode(request.CommandLine, terminators: 1);
            environment = AllocateUnicode(request.EnvironmentBlock, terminators: 0);

            // Suspended: it must be in the job before it runs a single instruction. The job's own limits are not combined with BREAKAWAY flags.
            var flags = NativeMethods.CreateSuspended | NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateNoWindow | NativeMethods.CreateUnicodeEnvironment;
            ProcessInformation created;
            var ok = restricted is null
                ? NativeMethods.CreateProcess(request.ApplicationPath, commandLine, 0, 0, true, flags, environment, request.WorkingDirectory, startupInfo, out created)
                : NativeMethods.CreateProcessAsUser(restricted, request.ApplicationPath, commandLine, 0, 0, true, flags, environment, request.WorkingDirectory, startupInfo, out created);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed");
            }

            var process = new Win32Process(
                new SafeFileHandle(created.Process, ownsHandle: true),
                new SafeFileHandle(created.Thread, ownsHandle: true),
                checked((int)created.ProcessId),
                new FileStream(outRead, FileAccess.Read, 4096, isAsync: false),
                new FileStream(errRead, FileAccess.Read, 4096, isAsync: false));
            succeeded = true;
            return process;
        }
        finally
        {
            if (attributeListReady)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }

            FreeHGlobal(attributeList);
            FreeHGlobal(handleList);
            FreeHGlobal(capabilities);
            FreeHGlobal(commandLine);
            FreeHGlobal(environment);
            FreeHGlobal(startupInfo);
            if (sid != 0)
            {
                NativeMethods.LocalFree(sid);
            }

            // The parent's copies of the child's ends are always closed; the read ends live on inside the streams only on success.
            nul?.Dispose();
            outWrite?.Dispose();
            errWrite?.Dispose();
            token?.Dispose();
            restricted?.Dispose();
            if (!succeeded)
            {
                outRead?.Dispose();
                errRead?.Dispose();
            }
        }
    }

    private static void CreateInheritablePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes)
    {
        if (!NativeMethods.CreatePipe(out read, out write, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed");
        }

        // The parent's read end must not be inherited.
        if (!NativeMethods.SetHandleInformation(read, NativeMethods.HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error, "SetHandleInformation failed");
        }
    }

    private static SafeFileHandle OpenOwnToken()
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenAssignPrimary | NativeMethods.TokenDuplicate | NativeMethods.TokenQuery,
                out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed");
        }

        return token;
    }

    private static void UpdateAttribute(nint list, nuint attribute, nint value, nuint size)
    {
        if (!NativeMethods.UpdateProcThreadAttribute(list, 0, attribute, value, size, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed");
        }
    }

    private static nint AllocateUnicode(string text, int terminators)
    {
        var bytes = (text.Length + terminators) * sizeof(char);
        var memory = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(text.ToCharArray(), 0, memory, text.Length);
        for (var i = 0; i < terminators; i++)
        {
            Marshal.WriteInt16(memory, (text.Length + i) * sizeof(char), 0);
        }

        return memory;
    }

    private static void FreeHGlobal(nint memory)
    {
        if (memory != 0)
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string SidToString(nint sid)
    {
        if (!NativeMethods.ConvertSidToStringSid(sid, out var text))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertSidToStringSid failed");
        }

        try
        {
            return Marshal.PtrToStringUni(text) ?? throw new InvalidOperationException("Empty SID string.");
        }
        finally
        {
            NativeMethods.LocalFree(text);
        }
    }

    internal static void SetInformation<T>(SafeFileHandle job, int informationClass, T value)
        where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, memory, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(job, informationClass, memory, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    internal static T QueryInformation<T>(SafeFileHandle job, int informationClass)
        where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(job, informationClass, memory, (uint)size, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed");
            }

            return Marshal.PtrToStructure<T>(memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class Win32Job(SafeFileHandle handle) : IWindowsJob
    {
        private const int _maxListedIds = 256;
        private const int _errorMoreData = 234;
        private int _disposed;

        public WindowsJobLimits QueryLimits()
        {
            var extended = QueryInformation<JobExtendedLimitInformation>(handle, NativeMethods.JobObjectExtendedLimitInformation);
            var ui = QueryInformation<JobBasicUiRestrictions>(handle, NativeMethods.JobObjectBasicUiRestrictions);
            var flags = extended.BasicLimitInformation.LimitFlags;
            return new WindowsJobLimits(
                (flags & NativeMethods.JobLimitKillOnJobClose) != 0,
                (flags & (NativeMethods.JobLimitBreakawayOk | NativeMethods.JobLimitSilentBreakawayOk)) != 0,
                (flags & NativeMethods.JobLimitDieOnUnhandledException) != 0,
                (flags & NativeMethods.JobLimitActiveProcess) != 0 ? (int)extended.BasicLimitInformation.ActiveProcessLimit : -1,
                (flags & NativeMethods.JobLimitProcessMemory) != 0 ? (long)extended.ProcessMemoryLimit : -1,
                (flags & NativeMethods.JobLimitJobMemory) != 0 ? (long)extended.JobMemoryLimit : -1,
                (flags & NativeMethods.JobLimitJobTime) != 0 ? TimeSpan.FromTicks(extended.BasicLimitInformation.PerJobUserTimeLimit) : TimeSpan.Zero,
                ui.UiRestrictionsClass);
        }

        public IReadOnlyList<int> ProcessIds()
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: DWORD assigned, DWORD listed, then ULONG_PTR ids.
            var size = 8 + (_maxListedIds * nint.Size);
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(handle, NativeMethods.JobObjectBasicProcessIdList, memory, (uint)size, out _)
                    && Marshal.GetLastPInvokeError() != _errorMoreData)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject (process ids) failed");
                }

                var listed = Marshal.ReadInt32(memory, 4);
                var ids = new List<int>(listed);
                for (var i = 0; i < listed; i++)
                {
                    ids.Add(checked((int)Marshal.ReadIntPtr(memory, 8 + (i * nint.Size))));
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        public void Assign(IWindowsProcess process)
        {
            var native = process as Win32Process ?? throw new ArgumentException("Not a Win32 process.", nameof(process));
            if (!NativeMethods.AssignProcessToJobObject(handle, native.Handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
            }
        }

        public void Terminate(uint exitCode)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!NativeMethods.TerminateJobObject(handle, exitCode))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "TerminateJobObject failed");
            }
        }

        public void Dispose()
        {
            // Closing the last handle kills every process (kill-on-close).
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                handle.Dispose();
            }
        }
    }

    private sealed class Win32Process(SafeFileHandle process, SafeFileHandle thread, int id, Stream stdout, Stream stderr) : IWindowsProcess
    {
        private int _disposed;

        public int Id => id;

        public Stream StandardOutput => stdout;

        public Stream StandardError => stderr;

        internal SafeFileHandle Handle => process;

        public bool HasExited => Volatile.Read(ref _disposed) != 0 || NativeMethods.WaitForSingleObject(process, 0) == NativeMethods.WaitObject0;

        public int ExitCode
        {
            get
            {
                if (!NativeMethods.GetExitCodeProcess(process, out var code))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed");
                }

                return unchecked((int)code);
            }
        }

        public void Resume()
        {
            if (NativeMethods.ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread failed");
            }

            thread.Dispose();
        }

        public void Terminate(uint exitCode)
        {
            if (Volatile.Read(ref _disposed) != 0 || HasExited)
            {
                return;
            }

            if (!NativeMethods.TerminateProcess(process, exitCode) && !HasExited)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "TerminateProcess failed");
            }
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (HasExited)
                {
                    return true;
                }

                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            stdout.Dispose();
            stderr.Dispose();
            thread.Dispose();
            process.Dispose();
        }
    }
}
