using System.Runtime.InteropServices;
using System.Text;

namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>A failed hardening step: the exit code to use and a one-line reason for stderr.</summary>
internal sealed record HardeningFailure(int ExitCode, string Message);

/// <summary>The kernel interfaces the helper drives: no_new_privs, Landlock, seccomp-bpf. Each step reports failure; none is skipped silently.</summary>
internal static unsafe class Hardening
{
    private const int _sysLandlockCreateRuleset = 444;
    private const int _sysLandlockAddRule = 445;
    private const int _sysLandlockRestrictSelf = 446;
    private const int _sysCloseRange = 436;
    private const uint _landlockCreateRulesetVersion = 1;
    private const int _landlockRulePathBeneath = 1;
    private const int _seccompSetModeFilter = 1;
    private const long _seccompFilterFlagTsync = 1;

    private const int _enoent = 2;
    private const int _enosys = 38;
    private const int _eopnotsupp = 95;

    /// <summary>Devices every target may read besides the policy paths (no data of the host, no execution).</summary>
    internal static readonly string[] DeviceReadPaths = ["/dev/zero", "/dev/urandom", "/dev/random"];

    internal static int SeccompSyscallNumber(SeccompArch arch) => arch == SeccompArch.X64 ? 317 : 277;

    /// <summary>Sets PR_SET_NO_NEW_PRIVS and verifies it reads back as 1.</summary>
    internal static HardeningFailure? SetNoNewPrivs()
    {
        if (Native.Prctl(Native.PrSetNoNewPrivs, 1) != 0)
        {
            return new(HelperExit.NoNewPrivsFailed, $"PR_SET_NO_NEW_PRIVS failed (errno {Native.Errno})");
        }

        if (Native.Prctl(Native.PrGetNoNewPrivs) != 1)
        {
            return new(HelperExit.NoNewPrivsFailed, "no_new_privs did not read back as set");
        }

        return null;
    }

    /// <summary>The Landlock ABI version the kernel reports, or a value below 1 when Landlock is unavailable (the errno is in <paramref name="errno"/>).</summary>
    internal static int QueryLandlockAbi(out int errno)
    {
        var abi = Native.Syscall(_sysLandlockCreateRuleset, 0, 0, _landlockCreateRulesetVersion);
        errno = abi < 0 ? Native.Errno : 0;
        return (int)abi;
    }

    /// <summary>Opens every rule path and builds the ruleset, without enforcing it. Returns the ruleset fd.</summary>
    internal static HardeningFailure? CreateLandlockRuleset(HelperPolicy policy, int abi, out int rulesetFd)
    {
        rulesetFd = -1;
        var attr = stackalloc ulong[3];
        attr[0] = LandlockRights.HandledFs(abi);
        attr[1] = LandlockRights.HandledNet(abi);
        attr[2] = LandlockRights.Scopes(abi);
        var fd = (int)Native.Syscall(_sysLandlockCreateRuleset, (long)attr, LandlockRights.RulesetAttrSize(abi), 0);
        if (fd < 0)
        {
            return new(HelperExit.LandlockFailed, $"landlock_create_ruleset failed (errno {Native.Errno})");
        }

        rulesetFd = fd;
        foreach (var path in policy.ReadPaths)
        {
            var failure = AddPath(fd, path, LandlockRights.ReadExec(abi), abi, mustExist: false);
            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var path in policy.WritePaths)
        {
            var failure = AddPath(fd, path, LandlockRights.ReadWrite(abi), abi, mustExist: true);
            if (failure is not null)
            {
                return failure;
            }
        }

        // Fixed, harmless character devices: /dev/null may be written (shell redirections), the others only read.
        var devNull = AddPath(fd, "/dev/null", LandlockRights.ReadWrite(abi), abi, mustExist: false);
        if (devNull is not null)
        {
            return devNull;
        }

        foreach (var device in DeviceReadPaths)
        {
            var failure = AddPath(fd, device, LandlockRights.ReadExec(abi) & ~LandlockRights.Execute, abi, mustExist: false);
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static HardeningFailure? AddPath(int rulesetFd, string path, ulong rights, int abi, bool mustExist)
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            // A missing allowed path only narrows access, so a read path may be absent; a write path is a configuration error.
            return mustExist ? new(HelperExit.LandlockFailed, $"write path does not exist: {path}") : null;
        }

        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        int pathFd;
        fixed (byte* p = bytes)
        {
            pathFd = Native.Open(p, Native.OPath | Native.OCloexec);
        }

        if (pathFd < 0)
        {
            var errno = Native.Errno;
            return !mustExist && errno == _enoent ? null : new(HelperExit.LandlockFailed, $"cannot open {path} for Landlock (errno {errno})");
        }

        try
        {
            var rule = stackalloc byte[16];
            new Span<byte>(rule, 16).Clear();
            *(ulong*)rule = LandlockRights.ForPath(rights, abi, isDirectory);
            *(int*)(rule + 8) = pathFd;
            var result = Native.Syscall(_sysLandlockAddRule, rulesetFd, _landlockRulePathBeneath, (long)rule, 0);
            return result < 0 ? new(HelperExit.LandlockFailed, $"landlock_add_rule failed for {path} (errno {Native.Errno})") : null;
        }
        finally
        {
            _ = Native.Close(pathFd);
        }
    }

    /// <summary>Enforces the ruleset on this thread (and what it execs) and closes the fd.</summary>
    internal static HardeningFailure? RestrictSelf(int rulesetFd)
    {
        var result = Native.Syscall(_sysLandlockRestrictSelf, rulesetFd, 0);
        var errno = result < 0 ? Native.Errno : 0;
        _ = Native.Close(rulesetFd);
        return result < 0 ? new(HelperExit.LandlockFailed, $"landlock_restrict_self failed (errno {errno})") : null;
    }

    /// <summary>Marks every fd above stderr close-on-exec, so nothing opened by the helper's runtime reaches the target. A failure stops the launch.</summary>
    internal static HardeningFailure? MarkDescriptorsCloseOnExec() =>
        Native.Syscall(_sysCloseRange, 3, uint.MaxValue, Native.CloseRangeCloexec) < 0
            ? new(HelperExit.InitFailed, $"close_range(CLOEXEC) failed (errno {Native.Errno}); refusing to leak descriptors into the target")
            : null;

    /// <summary>Allocates the BPF program in unmanaged memory in <c>struct sock_fprog</c> form (length, padding, pointer).</summary>
    internal static byte* PrepareFilter(BpfInstruction[] program)
    {
        var size = (nuint)(program.Length * sizeof(BpfInstruction));
        var instructions = (BpfInstruction*)NativeMemory.Alloc(size);
        for (var i = 0; i < program.Length; i++)
        {
            instructions[i] = program[i];
        }

        var fprog = (byte*)NativeMemory.AllocZeroed(16);
        *(ushort*)fprog = checked((ushort)program.Length);
        *(BpfInstruction**)(fprog + 8) = instructions;
        return fprog;
    }

    /// <summary>Installs the filter on all threads of the process (TSYNC). Any non-zero result is a failure: a positive value is the tid of a thread that could not be synced.</summary>
    internal static HardeningFailure? InstallSeccomp(SeccompArch arch, byte* fprog)
    {
        var result = Native.Syscall(SeccompSyscallNumber(arch), _seccompSetModeFilter, _seccompFilterFlagTsync, (long)fprog);
        if (result == 0)
        {
            return null;
        }

        return new(HelperExit.SeccompFailed, result > 0
            ? $"seccomp TSYNC failed: thread {result} could not be synchronised"
            : $"seccomp filter install failed (errno {Native.Errno})");
    }

    internal static bool LandlockMissing(int errno) => errno is _enosys or _eopnotsupp;
}
