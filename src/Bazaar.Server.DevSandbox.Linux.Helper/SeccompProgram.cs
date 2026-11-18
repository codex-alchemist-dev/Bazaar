using System.Runtime.InteropServices;

namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>One classic BPF instruction (<c>struct sock_filter</c>, 8 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BpfInstruction
{
    public ushort Code;
    public byte Jt;
    public byte Jf;
    public uint K;

    public BpfInstruction(ushort code, byte jt, byte jf, uint k)
    {
        Code = code;
        Jt = jt;
        Jf = jf;
        K = k;
    }
}

/// <summary>
/// The seccomp allow list for a computation-only process, and the builder that turns it into a BPF program. Built in managed code,
/// before Landlock and seccomp are applied.
/// <para>
/// Program shape: (1) load the architecture and kill the process on mismatch; (2) load the syscall number; (3) explicit deny rules for
/// dangerous syscalls (kill), so a later edit that wrongly allows one still hits them first; (4) the allow list in order, where a few
/// syscalls carry argument checks (<c>ioctl</c>, <c>prctl</c>, <c>clone</c>), and <c>clone3</c> returns ENOSYS so libc falls back to
/// <c>clone</c>, whose flags the filter can see; (5) default action <c>SECCOMP_RET_KILL_PROCESS</c>. On x86_64 the x32 ABI numbers
/// (bit 30 set) match no rule and are killed by the default.
/// </para>
/// </summary>
internal static class SeccompProgram
{
    internal const uint AuditArchX86_64 = 0xC000003E;
    internal const uint AuditArchAarch64 = 0xC00000B7;

    internal const uint RetKillProcess = 0x80000000;
    internal const uint RetAllow = 0x7FFF0000;
    internal const uint RetErrnoBase = 0x00050000;
    internal const int Enosys = 38;

    // Classic BPF opcodes.
    internal const ushort LdWAbs = 0x20;
    internal const ushort JeqK = 0x15;
    internal const ushort JsetK = 0x45;
    internal const ushort RetK = 0x06;

    private const uint _offsetNr = 0;
    private const uint _offsetArch = 4;
    private const uint _offsetArgs = 16;

    // CLONE_NEWNS | CLONE_NEWCGROUP | CLONE_NEWUTS | CLONE_NEWIPC | CLONE_NEWUSER | CLONE_NEWPID | CLONE_NEWNET | CLONE_NEWTIME
    internal const uint CloneNamespaceMask = 0x00020000 | 0x02000000 | 0x04000000 | 0x08000000 | 0x10000000 | 0x20000000 | 0x40000000 | 0x00000080;

    /// <summary>The base allow list: read, write, memory, signals, time, futex, process basics and exec. No network, no mount, no ptrace.</summary>
    internal static readonly string[] BaseAllow =
    [
        // file descriptors and file system (Landlock decides which paths)
        "read", "write", "readv", "writev", "pread64", "pwrite64", "preadv", "pwritev", "preadv2", "pwritev2",
        "open", "openat", "openat2", "close", "close_range", "lseek", "stat", "fstat", "lstat", "newfstatat", "statx",
        "access", "faccessat", "faccessat2", "readlink", "readlinkat", "getdents", "getdents64", "getcwd", "chdir", "fchdir",
        "pipe", "pipe2", "dup", "dup2", "dup3", "fcntl", "flock", "fsync", "fdatasync", "ftruncate", "truncate",
        "rename", "renameat", "renameat2", "mkdir", "mkdirat", "rmdir", "unlink", "unlinkat", "symlink", "symlinkat",
        "link", "linkat", "chmod", "fchmod", "fchmodat", "umask", "utimensat", "fallocate", "fadvise64", "readahead",
        "sendfile", "copy_file_range", "statfs", "fstatfs",

        // memory
        "mmap", "munmap", "mprotect", "mremap", "madvise", "brk", "mincore", "mlock", "munlock", "msync", "membarrier",

        // signals
        "rt_sigaction", "rt_sigprocmask", "rt_sigreturn", "rt_sigpending", "rt_sigtimedwait", "rt_sigsuspend", "sigaltstack",
        "kill", "tgkill", "tkill", "pause",

        // process and thread basics (clone is argument-checked below, clone3 is answered with ENOSYS)
        "exit", "exit_group", "wait4", "waitid", "execve", "execveat", "fork", "vfork",
        "getpid", "getppid", "gettid", "getuid", "geteuid", "getgid", "getegid", "getgroups", "getpgid", "getpgrp", "setpgid",
        "getsid", "setsid", "uname", "sysinfo", "getrlimit", "prlimit64", "getrusage", "times", "sched_yield",
        "sched_getaffinity", "sched_getparam", "sched_getscheduler", "sched_get_priority_max", "sched_get_priority_min",
        "getcpu", "set_tid_address", "set_robust_list", "get_robust_list", "rseq", "arch_prctl",

        // time, synchronisation, readiness, randomness
        "clock_gettime", "clock_getres", "clock_nanosleep", "gettimeofday", "time", "nanosleep", "getitimer", "setitimer",
        "futex", "restart_syscall", "eventfd", "eventfd2", "epoll_create", "epoll_create1", "epoll_ctl", "epoll_wait", "epoll_pwait",
        "poll", "ppoll", "select", "pselect6", "getrandom",
    ];

    /// <summary>Syscalls a policy may add to the allow list by name. Anything else, and anything on <see cref="Denied"/>, is refused.</summary>
    internal static readonly string[] Optional =
    [
        "memfd_create", "sched_setaffinity", "timer_create", "timer_settime", "timer_gettime", "timer_delete",
        "timerfd_create", "timerfd_settime", "timerfd_gettime", "inotify_init1", "inotify_add_watch", "inotify_rm_watch",
        "getpriority", "setpriority", "capget",
    ];

    /// <summary>Syscalls that are always denied with an explicit kill rule and can never be added by a policy.</summary>
    internal static readonly string[] Denied =
    [
        "ptrace", "mount", "umount2", "pivot_root", "unshare", "setns", "bpf", "perf_event_open", "keyctl", "add_key", "request_key",
        "kexec_load", "kexec_file_load", "init_module", "finit_module", "delete_module",
        "socket", "socketpair", "connect", "bind", "listen", "accept", "accept4", "sendto", "recvfrom", "sendmsg", "recvmsg",
        "sendmmsg", "recvmmsg", "setsockopt", "getsockopt", "shutdown",
        "open_by_handle_at", "name_to_handle_at", "process_vm_readv", "process_vm_writev", "userfaultfd", "chroot", "reboot",
        "swapon", "swapoff", "acct", "iopl", "ioperm", "io_uring_setup", "io_uring_enter", "io_uring_register",
        "move_mount", "open_tree", "fsopen", "fsconfig", "fsmount", "fspick", "mount_setattr", "syslog", "settimeofday",
        "clock_settime", "clock_adjtime", "adjtimex", "sethostname", "setdomainname", "quotactl", "kcmp",
        "seccomp", "landlock_create_ruleset", "landlock_add_rule", "landlock_restrict_self",
    ];

    /// <summary>The <c>ioctl</c> commands that stay allowed: terminal queries, FIONREAD, FIONBIO, close-on-exec flags.</summary>
    internal static readonly uint[] IoctlAllowed = [0x5401, 0x5413, 0x541B, 0x5421, 0x5450, 0x5451];

    /// <summary>The <c>prctl</c> options that stay allowed: thread name, no_new_privs query/set, seccomp query, VMA naming, tid address.</summary>
    internal static readonly uint[] PrctlAllowed = [15, 16, 38, 39, 21, 40, 0x53564D41];

    /// <summary>Detects the running architecture, or null when unsupported.</summary>
    internal static SeccompArch? DetectArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => SeccompArch.X64,
        Architecture.Arm64 => SeccompArch.Arm64,
        _ => null,
    };

    internal static uint AuditArch(SeccompArch arch) => arch == SeccompArch.X64 ? AuditArchX86_64 : AuditArchAarch64;

    /// <summary>Builds the program for <paramref name="arch"/>. <paramref name="extraAllowed"/> must already be validated (see <see cref="Optional"/>).</summary>
    internal static BpfInstruction[] Build(SeccompArch arch, IEnumerable<string> extraAllowed)
    {
        ArgumentNullException.ThrowIfNull(extraAllowed);
        var p = new List<BpfInstruction>
        {
            Stmt(LdWAbs, _offsetArch),
            Jump(JeqK, AuditArch(arch), 1, 0),
            Stmt(RetK, RetKillProcess),
            Stmt(LdWAbs, _offsetNr),
        };

        // Explicit denials first.
        foreach (var name in Denied)
        {
            if (SyscallTable.TryGetNumber(name, arch, out var nr))
            {
                p.Add(Jump(JeqK, (uint)nr, 0, 1));
                p.Add(Stmt(RetK, RetKillProcess));
            }
        }

        // clone3 cannot be inspected (flags live in user memory): tell libc it does not exist so it uses clone.
        if (SyscallTable.TryGetNumber("clone3", arch, out var clone3))
        {
            p.Add(Jump(JeqK, (uint)clone3, 0, 1));
            p.Add(Stmt(RetK, RetErrnoBase | Enosys));
        }

        // clone: allowed only when no namespace flag is set.
        if (SyscallTable.TryGetNumber("clone", arch, out var clone))
        {
            p.Add(Jump(JeqK, (uint)clone, 0, 4));
            p.Add(Stmt(LdWAbs, _offsetArgs));
            p.Add(Jump(JsetK, CloneNamespaceMask, 1, 0));
            p.Add(Stmt(RetK, RetAllow));
            p.Add(Stmt(RetK, RetKillProcess));
        }

        AddArgChecked(p, arch, "ioctl", 1, IoctlAllowed);
        AddArgChecked(p, arch, "prctl", 0, PrctlAllowed);

        foreach (var name in BaseAllow.Concat(extraAllowed).Distinct(StringComparer.Ordinal))
        {
            if (SyscallTable.TryGetNumber(name, arch, out var nr))
            {
                p.Add(Jump(JeqK, (uint)nr, 0, 1));
                p.Add(Stmt(RetK, RetAllow));
            }
        }

        p.Add(Stmt(RetK, RetKillProcess));
        return p.ToArray();
    }

    private static void AddArgChecked(List<BpfInstruction> p, SeccompArch arch, string name, int argIndex, uint[] allowedValues)
    {
        if (!SyscallTable.TryGetNumber(name, arch, out var nr))
        {
            return;
        }

        // [jeq nr] [ld arg] [jeq v0 .. jeq vk] [kill] [allow]
        var skip = 1 + allowedValues.Length + 2;
        p.Add(Jump(JeqK, (uint)nr, 0, checked((byte)skip)));
        p.Add(Stmt(LdWAbs, _offsetArgs + (8u * (uint)argIndex)));
        for (var i = 0; i < allowedValues.Length; i++)
        {
            // Jump over the remaining compares and the kill to the allow.
            p.Add(Jump(JeqK, allowedValues[i], checked((byte)(allowedValues.Length - 1 - i + 1)), 0));
        }

        p.Add(Stmt(RetK, RetKillProcess));
        p.Add(Stmt(RetK, RetAllow));
    }

    private static BpfInstruction Stmt(ushort code, uint k) => new(code, 0, 0, k);

    private static BpfInstruction Jump(ushort code, uint k, byte jt, byte jf) => new(code, jt, jf, k);
}
