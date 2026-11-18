using System.Collections;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// Local shells under a Windows pseudo console (ConPTY): pipes, <c>CreatePseudoConsole</c>, a
/// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> attribute on <c>CreateProcessW</c>, and a kill-on-close job object for the
/// process tree. Needs Windows 10 version 1809 (build 17763) or later.
/// </summary>
/// <remarks>
/// This backend cannot run in the Linux build container. Its tests run on the Windows lane only (doc 52, section 8).
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ConPtyBackend : IPtyBackend
{
    /// <summary>True on Windows 10 build 17763 or later.</summary>
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <inheritdoc />
    public IPtySession Open(TerminalSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("ConPTY needs Windows 10 build 17763 or later.");
        }

        var shell = ShellResolver.Resolve(spec.Shell, isWindows: true);
        var cwd = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : spec.WorkingDirectory;
        var env = EnvironmentFilter.Build(InheritedEnvironment(), spec.Environment, StringComparer.OrdinalIgnoreCase);
        if (!env.ContainsKey("TERM"))
        {
            env["TERM"] = "xterm-256color";
        }

        // The host process ignores Ctrl-C when it starts from a console that does. Children inherit that flag, so a
        // Ctrl-C written to the pseudo console would be ignored by the shell (S11 finding). Clear it first.
        Win32Native.SetConsoleCtrlHandler(nint.Zero, 0);

        nint inputRead = nint.Zero, inputWrite = nint.Zero, outputRead = nint.Zero, outputWrite = nint.Zero;
        nint pseudoConsole = nint.Zero, attributes = nint.Zero, job = nint.Zero;
        try
        {
            if (Win32Native.CreatePipe(out inputRead, out inputWrite, nint.Zero, 0) == 0 ||
                Win32Native.CreatePipe(out outputRead, out outputWrite, nint.Zero, 0) == 0)
            {
                throw LastError("CreatePipe");
            }

            ThrowIfFailed(Win32Native.CreatePseudoConsole(ToCoord(spec.Cols, spec.Rows), inputRead, outputWrite, 0, out pseudoConsole), "CreatePseudoConsole");

            // The pseudo console keeps its own copies of these two ends.
            Win32Native.CloseHandle(inputRead);
            inputRead = nint.Zero;
            Win32Native.CloseHandle(outputWrite);
            outputWrite = nint.Zero;

            attributes = CreateAttributeList(pseudoConsole);
            job = CreateKillOnCloseJob();

            var startup = new StartupInfoEx
            {
                Cb = (uint)Marshal.SizeOf<StartupInfoEx>(),
                AttributeList = attributes,

                // Null standard handles with this flag stop the shell from adopting the host's own (possibly redirected)
                // stdout and stdin instead of the pseudo console.
                Flags = Win32Native.StartfUseStdHandles,
            };
            var commandLine = Marshal.StringToHGlobalUni($"\"{shell}\"");
            var environment = Marshal.StringToHGlobalUni(EnvironmentFilter.WindowsBlock(env));
            var directory = Marshal.StringToHGlobalUni(cwd);
            ProcessInformation info = default;
            try
            {
                bool created;
                unsafe
                {
                    created = Win32Native.CreateProcessW(
                        nint.Zero,
                        commandLine,
                        nint.Zero,
                        nint.Zero,
                        0,
                        Win32Native.ExtendedStartupInfoPresent | Win32Native.CreateUnicodeEnvironment | Win32Native.CreateSuspended,
                        environment,
                        directory,
                        &startup,
                        &info) != 0;
                }

                if (!created)
                {
                    throw LastError("CreateProcessW");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(commandLine);
                Marshal.FreeHGlobal(environment);
                Marshal.FreeHGlobal(directory);
            }

            // Assign before the shell runs, so the whole tree is inside the job from its first moment.
            if (Win32Native.AssignProcessToJobObject(job, info.Process) == 0)
            {
                Win32Native.TerminateProcess(info.Process, 1);
                throw LastError("AssignProcessToJobObject");
            }

            Win32Native.ResumeThread(info.Thread);
            Win32Native.CloseHandle(info.Thread);

            var session = new ConPtySession(pseudoConsole, inputWrite, outputRead, info.Process, info.ProcessId, job, attributes);
            inputWrite = nint.Zero;
            outputRead = nint.Zero;
            pseudoConsole = nint.Zero;
            job = nint.Zero;
            attributes = nint.Zero;
            return session;
        }
        finally
        {
            // Only non-zero handles here are ones the session did not take over (the failure path).
            Release(inputRead);
            Release(inputWrite);
            Release(outputRead);
            Release(outputWrite);
            Release(job);
            if (attributes != nint.Zero)
            {
                Win32Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (pseudoConsole != nint.Zero)
            {
                Win32Native.ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    internal static Coord ToCoord(int cols, int rows) => new()
    {
        X = (short)Math.Clamp(cols, 1, short.MaxValue),
        Y = (short)Math.Clamp(rows, 1, short.MaxValue),
    };

    private static Dictionary<string, string> InheritedEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static nint CreateAttributeList(nint pseudoConsole)
    {
        nuint size = 0;
        unsafe
        {
            // The first call only reports the size; it fails by design.
            Win32Native.InitializeProcThreadAttributeList(nint.Zero, 1, 0, &size);
        }

        if (Marshal.GetLastPInvokeError() != Win32Native.ErrorInsufficientBuffer)
        {
            throw LastError("InitializeProcThreadAttributeList(size)");
        }

        var list = Marshal.AllocHGlobal((nint)size);
        bool ok;
        unsafe
        {
            ok = Win32Native.InitializeProcThreadAttributeList(list, 1, 0, &size) != 0;
        }

        if (!ok)
        {
            Marshal.FreeHGlobal(list);
            throw LastError("InitializeProcThreadAttributeList");
        }

        // The attribute value is the HPCON itself, not a pointer to it.
        if (Win32Native.UpdateProcThreadAttribute(list, 0, Win32Native.ProcThreadAttributePseudoConsole, pseudoConsole, (nuint)nint.Size, nint.Zero, nint.Zero) == 0)
        {
            Win32Native.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw LastError("UpdateProcThreadAttribute");
        }

        return list;
    }

    private static nint CreateKillOnCloseJob()
    {
        var job = Win32Native.CreateJobObjectW(nint.Zero, nint.Zero);
        if (job == nint.Zero)
        {
            throw LastError("CreateJobObjectW");
        }

        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = Win32Native.JobObjectLimitKillOnJobClose;
        unsafe
        {
            if (Win32Native.SetInformationJobObject(job, Win32Native.JobObjectExtendedLimitInformation, &limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) == 0)
            {
                Win32Native.CloseHandle(job);
                throw LastError("SetInformationJobObject");
            }
        }

        return job;
    }

    private static void ThrowIfFailed(int hresult, string call)
    {
        if (hresult < 0)
        {
            throw new InvalidOperationException($"{call} failed with HRESULT 0x{hresult:X8}.");
        }
    }

    private static void Release(nint handle)
    {
        if (handle != nint.Zero)
        {
            Win32Native.CloseHandle(handle);
        }
    }

    private static InvalidOperationException LastError(string call)
    {
        var error = Marshal.GetLastPInvokeError();
        return new InvalidOperationException($"{call} failed: {Marshal.GetPInvokeErrorMessage(error)} (Win32 error {error}).");
    }
}
