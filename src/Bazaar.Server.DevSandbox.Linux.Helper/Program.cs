using System.Runtime.InteropServices;
using System.Text;

namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>
/// Entry point of the Linux sandbox hardening helper (doc 48 section 2.1, "Linux provider"). Usage:
/// <c>helper --policy &lt;file&gt; [--delete-policy-after-read] -- &lt;target&gt; [args]</c>.
/// <para>
/// .NET cannot run code between fork and exec, so the provider execs this helper inside its unshare/prlimit wrapper. The helper reads
/// the policy, then, in this order: sets no_new_privs; enforces a Landlock ruleset; applies the address-space limit; marks stray fds
/// close-on-exec; installs the seccomp allow list on all threads; and execs the target with an empty environment except PATH. Landlock and
/// seccomp survive the exec. Every step either succeeds or the helper exits non-zero without running the target: there is no downgrade.
/// </para>
/// </summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        var parsed = new HelperArguments();
        var usageError = parsed.Parse(args);
        if (usageError is not null)
        {
            return Fail(HelperExit.Usage, usageError);
        }

        var initError = Native.Initialize();
        if (initError is not null)
        {
            return Fail(HelperExit.InitFailed, initError);
        }

        if (parsed.Info)
        {
            var infoAbi = Hardening.QueryLandlockAbi(out _);
            Console.Out.WriteLine($"landlock-abi={infoAbi}");
            Console.Out.WriteLine($"arch={RuntimeInformation.ProcessArchitecture}");
            return 0;
        }

        var policy = ReadPolicy(parsed, out var policyFailure);
        if (policy is null)
        {
            return policyFailure;
        }

        var arch = SeccompProgram.DetectArch();
        if (arch is null)
        {
            return Fail(HelperExit.UnsupportedArch, $"unsupported architecture {RuntimeInformation.ProcessArchitecture}; the seccomp filter covers x86_64 and arm64 only");
        }

        // Everything that needs the managed runtime is prepared now. After Landlock the helper only makes raw calls.
        var program = SeccompProgram.Build(arch.Value, policy.ExtraSyscalls);
        var prepared = Prepare(parsed, policy, program);

        var failure = Hardening.SetNoNewPrivs();
        if (failure is not null)
        {
            return Fail(failure);
        }

        var abi = Hardening.QueryLandlockAbi(out var abiErrno);
        if (parsed.SimulateNoLandlock)
        {
            abi = -1;
            abiErrno = 38;
        }

        if (abi < 1)
        {
            return Fail(HelperExit.LandlockUnavailable, $"Landlock is not available (errno {abiErrno}); refusing to run without file system confinement");
        }

        failure = Hardening.CreateLandlockRuleset(policy, abi, out var rulesetFd);
        if (failure is not null)
        {
            return Fail(failure);
        }

        // No core dumps from the target. Also runs the setrlimit call once now, so the address-space call below is already compiled:
        // the helper's own runtime reserves far more than a module's limit, so after RLIMIT_AS is lowered nothing new may be mapped.
        if (Native.SetRlimit(Native.RlimitCore, 0, 0) != 0)
        {
            return Fail(HelperExit.RlimitFailed, $"setrlimit(RLIMIT_CORE) failed (errno {Native.Errno})");
        }

        Hardening.MarkDescriptorsCloseOnExec();
        failure = Hardening.RestrictSelf(rulesetFd);
        if (failure is not null)
        {
            return Fail(failure);
        }

        // From here on: raw calls only. A failure ends the process through _exit and never reaches the target.
        unsafe
        {
            failure = Hardening.InstallSeccomp(arch.Value, prepared.Filter);
            if (failure is not null)
            {
                Native.WriteError(PreparedExec.Message(failure.Message));
                Native.Exit(failure.ExitCode);
            }

            // Last step before exec, after the filter: lowering RLIMIT_AS below what the helper's runtime already reserved means no further
            // mapping can succeed in this process, so nothing but the exec call may follow.
            if (prepared.AddressSpace != 0 && Native.SetRlimit(Native.RlimitAs, prepared.AddressSpace, prepared.AddressSpace) != 0)
            {
                Native.WriteError(prepared.RlimitFailedMessage);
                Native.Exit(HelperExit.RlimitFailed);
            }

            if (parsed.SelfTest is not null)
            {
                Native.WriteError(prepared.Ready);
                var fd = Native.Socket(2, 1, 0);
                Native.WriteError(prepared.SocketAllowed);
                Native.Exit(HelperExit.SelfTestNotDenied);
                return fd;
            }

            _ = Native.Execve(prepared.TargetPath, prepared.Argv, prepared.Envp);
            Native.WriteError(PreparedExec.Message($"execve failed (errno {Native.Errno})"));
            Native.Exit(HelperExit.ExecFailed);
        }

        return HelperExit.ExecFailed;
    }

    private static HelperPolicy? ReadPolicy(HelperArguments parsed, out int failureCode)
    {
        failureCode = 0;
        string json;
        try
        {
            json = File.ReadAllText(parsed.PolicyPath!, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failureCode = Fail(HelperExit.PolicyUnreadable, "cannot read policy: " + ex.Message);
            return null;
        }

        if (parsed.DeletePolicyAfterRead)
        {
            try
            {
                File.Delete(parsed.PolicyPath!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failureCode = Fail(HelperExit.PolicyUnreadable, "cannot delete policy after reading: " + ex.Message);
                return null;
            }
        }

        var policy = HelperPolicyParser.Parse(json, out var error);
        if (policy is null)
        {
            failureCode = Fail(HelperExit.PolicyInvalid, error ?? "invalid policy");
        }

        return policy;
    }

    private static unsafe PreparedExec Prepare(HelperArguments parsed, HelperPolicy policy, BpfInstruction[] filterProgram)
    {
        var target = parsed.Target ?? "/";
        var argv = new List<string> { target };
        argv.AddRange(parsed.TargetArguments);
        var envp = new List<string> { "PATH=" + policy.Path };
        return new PreparedExec(
            CString(target),
            CStringArray(argv),
            CStringArray(envp),
            Hardening.PrepareFilter(filterProgram),
            (ulong)(policy.AddressSpaceBytes ?? 0),
            Encoding.UTF8.GetBytes("bazaar-sandbox-helper: setrlimit(RLIMIT_AS) failed\n"),
            Encoding.UTF8.GetBytes("bazaar-sandbox-helper: ready\n"),
            Encoding.UTF8.GetBytes("bazaar-sandbox-helper: SOCKET_ALLOWED\n"));
    }

    private static unsafe byte* CString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var p = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
        for (var i = 0; i < bytes.Length; i++)
        {
            p[i] = bytes[i];
        }

        return p;
    }

    private static unsafe byte** CStringArray(List<string> values)
    {
        var array = (byte**)NativeMemory.AllocZeroed((nuint)(values.Count + 1) * (nuint)sizeof(byte*));
        for (var i = 0; i < values.Count; i++)
        {
            array[i] = CString(values[i]);
        }

        return array;
    }

    private static int Fail(HardeningFailure failure) => Fail(failure.ExitCode, failure.Message);

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine("bazaar-sandbox-helper: " + message);
        return code;
    }

    /// <summary>Everything the post-restriction code needs, allocated beforehand.</summary>
    private sealed unsafe class PreparedExec(byte* targetPath, byte** argv, byte** envp, byte* filter, ulong addressSpace, byte[] rlimitFailedMessage, byte[] ready, byte[] socketAllowed)
    {
        internal byte* TargetPath { get; } = targetPath;

        internal byte** Argv { get; } = argv;

        internal byte** Envp { get; } = envp;

        internal byte* Filter { get; } = filter;

        internal ulong AddressSpace { get; } = addressSpace;

        internal byte[] RlimitFailedMessage { get; } = rlimitFailedMessage;

        internal byte[] Ready { get; } = ready;

        internal byte[] SocketAllowed { get; } = socketAllowed;

        internal static byte[] Message(string text) => Encoding.UTF8.GetBytes("bazaar-sandbox-helper: " + text + "\n");
    }
}
