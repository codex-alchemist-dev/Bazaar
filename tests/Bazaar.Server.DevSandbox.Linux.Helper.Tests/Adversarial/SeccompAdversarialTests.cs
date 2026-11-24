namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests.Adversarial;

/// <summary>Interprets the generated classic-BPF program and checks the verdict for hostile syscalls on both supported architectures.</summary>
public sealed class SeccompAdversarialTests
{
    private const uint _x32Bit = 0x40000000;

    // Kernel numbers (x86_64, arm64) from the uapi headers; -1 where the syscall does not exist on that architecture. Written out here
    // so the test does not trust SyscallTable for the names it is meant to check.
    private static readonly (string Name, int X64, int Arm64)[] _mustNeverRun =
    [
        ("ptrace", 101, 117), ("process_vm_readv", 310, 270), ("process_vm_writev", 311, 271), ("mount", 165, 40), ("umount2", 166, 39),
        ("pivot_root", 155, 41), ("chroot", 161, 51), ("unshare", 272, 97), ("setns", 308, 268), ("bpf", 321, 280),
        ("perf_event_open", 298, 241), ("userfaultfd", 323, 282), ("io_uring_setup", 425, 425), ("io_uring_enter", 426, 426),
        ("io_uring_register", 427, 427), ("keyctl", 250, 219), ("add_key", 248, 217), ("request_key", 249, 218), ("kexec_load", 246, 104),
        ("kexec_file_load", 320, 294), ("init_module", 175, 105), ("finit_module", 313, 273), ("delete_module", 176, 106),
        ("open_by_handle_at", 304, 265), ("name_to_handle_at", 303, 264), ("socket", 41, 198), ("socketpair", 53, 199),
        ("connect", 42, 203), ("bind", 49, 200), ("personality", 135, 92), ("memfd_secret", 447, 447), ("seccomp", 317, 277),
        ("fanotify_init", 300, 262), ("fanotify_mark", 301, 263), ("quotactl", 179, 60), ("swapon", 167, 224), ("swapoff", 168, 225),
        ("reboot", 169, 142), ("settimeofday", 164, 170), ("clock_settime", 227, 112), ("clock_adjtime", 305, 266), ("adjtimex", 159, 171),
        ("acct", 163, 89), ("sethostname", 170, 161), ("setdomainname", 171, 162), ("capset", 126, 91), ("setuid", 105, 146),
        ("setgid", 106, 144), ("setresuid", 117, 147), ("setreuid", 113, 145), ("setfsuid", 122, 151), ("setgroups", 116, 159),
        ("mknod", 133, -1), ("mknodat", 259, 33), ("chown", 92, -1), ("fchown", 93, 55), ("fchownat", 260, 54), ("pidfd_open", 434, 434),
        ("pidfd_getfd", 438, 438), ("pidfd_send_signal", 424, 424), ("mbind", 237, 235), ("set_mempolicy", 238, 237),
        ("move_pages", 279, 239), ("migrate_pages", 256, 238), ("lookup_dcookie", 212, 18), ("nfsservctl", 180, -1), ("syslog", 103, 116),
        ("kcmp", 312, 272), ("modify_ldt", 154, -1), ("iopl", 172, -1), ("ioperm", 173, -1), ("landlock_restrict_self", 446, 446),
        ("landlock_add_rule", 445, 445), ("landlock_create_ruleset", 444, 444), ("fsopen", 430, 430), ("fsmount", 432, 432),
        ("move_mount", 429, 429), ("open_tree", 428, 428), ("mount_setattr", 442, 442), ("sched_setattr", 314, 274),
        ("sched_setscheduler", 144, 119), ("mlockall", 151, 230), ("vhangup", 153, 58), ("umount", 166, -1), ("uselib", 134, -1),
    ];

    private const int _x64Ioctl = 16;
    private const int _arm64Ioctl = 29;
    private const int _x64Prctl = 157;
    private const int _arm64Prctl = 167;
    private const int _x64Clone = 56;
    private const int _arm64Clone = 220;
    private const int _x64Clone3 = 435;

    public static TheoryData<SeccompArch> Arches => new() { SeccompArch.X64, SeccompArch.Arm64 };

    private static uint Verdict(BpfInstruction[] program, uint arch, uint nr, ulong arg0 = 0, ulong arg1 = 0)
    {
        var data = new byte[64];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), nr);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), arch);
        BitConverter.TryWriteBytes(data.AsSpan(16, 8), arg0);
        BitConverter.TryWriteBytes(data.AsSpan(24, 8), arg1);
        uint a = 0;
        var pc = 0;
        for (var steps = 0; steps < 100000; steps++)
        {
            Assert.InRange(pc, 0, program.Length - 1);
            var i = program[pc];
            switch (i.Code)
            {
                case SeccompProgram.LdWAbs:
                    Assert.True(i.K % 4 == 0 && i.K + 4 <= data.Length, "load outside seccomp_data");
                    a = BitConverter.ToUInt32(data, checked((int)i.K));
                    pc++;
                    break;
                case SeccompProgram.JeqK:
                    pc += 1 + (a == i.K ? i.Jt : i.Jf);
                    break;
                case SeccompProgram.JsetK:
                    pc += 1 + ((a & i.K) != 0 ? i.Jt : i.Jf);
                    break;
                case SeccompProgram.RetK:
                    return i.K;
                default:
                    throw new InvalidOperationException("unexpected opcode " + i.Code);
            }
        }

        throw new InvalidOperationException("program did not terminate");
    }

    private static int Number((string Name, int X64, int Arm64) s, SeccompArch arch) => arch == SeccompArch.X64 ? s.X64 : s.Arm64;

    private static bool IsKill(uint verdict) => verdict == SeccompProgram.RetKillProcess;

    [Theory]
    [MemberData(nameof(Arches))]
    public void Every_hostile_syscall_is_killed_or_refused_never_allowed(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        foreach (var s in _mustNeverRun)
        {
            var nr = Number(s, arch);
            if (nr < 0)
            {
                continue;
            }

            var verdict = Verdict(program, SeccompProgram.AuditArch(arch), (uint)nr);
            Assert.True(IsKill(verdict), $"{s.Name} ({nr}) on {arch} got verdict 0x{verdict:X8}");
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void The_optional_catalogue_cannot_be_used_to_unlock_a_hostile_syscall(SeccompArch arch)
    {
        foreach (var name in SeccompProgram.Optional)
        {
            Assert.DoesNotContain(_mustNeverRun, s => s.Name == name);
        }

        // Asking for a denied name through the builder directly must not allow it either: the explicit kill rule comes first.
        var program = SeccompProgram.Build(arch, SeccompProgram.Denied);
        foreach (var name in SeccompProgram.Denied)
        {
            if (SyscallTable.TryGetNumber(name, arch, out var nr))
            {
                Assert.True(IsKill(Verdict(program, SeccompProgram.AuditArch(arch), (uint)nr)), name);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void The_x32_bit_kills_every_syscall_number_and_a_foreign_arch_kills_everything(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        var audit = SeccompProgram.AuditArch(arch);
        var foreign = new[] { 0u, 0x40000003u, 0x40000028u, 0xC000003Eu ^ 0x80000000u, 0x40000008u, 0xC00000B7u, 0xC000003Eu }.Where(a => a != audit);
        for (uint nr = 0; nr < 1024; nr++)
        {
            Assert.True(IsKill(Verdict(program, audit, nr | _x32Bit)), $"x32 bit set, nr {nr}");
            foreach (var other in foreign)
            {
                Assert.True(IsKill(Verdict(program, other, nr)), $"arch 0x{other:X}, nr {nr}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Only_named_syscalls_are_ever_allowed_and_everything_else_is_killed(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        var audit = SeccompProgram.AuditArch(arch);
        var allowedNames = new HashSet<string>(SeccompProgram.BaseAllow, StringComparer.Ordinal) { "clone", "ioctl", "prctl" };
        var numbersOfAllowed = new HashSet<int>();
        foreach (var name in allowedNames)
        {
            if (SyscallTable.TryGetNumber(name, arch, out var n))
            {
                numbersOfAllowed.Add(n);
            }
        }

        for (var nr = 0; nr < 1200; nr++)
        {
            var verdict = Verdict(program, audit, (uint)nr);
            var isEnosys = nr == (arch == SeccompArch.X64 ? _x64Clone3 : 435);
            if (isEnosys)
            {
                Assert.Equal(SeccompProgram.RetErrnoBase | SeccompProgram.Enosys, verdict);
            }
            else if (verdict == SeccompProgram.RetAllow)
            {
                Assert.Contains(nr, numbersOfAllowed);
            }
            else
            {
                Assert.True(IsKill(verdict), $"nr {nr}: 0x{verdict:X8}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Clone_with_any_namespace_flag_is_killed_and_clone3_is_never_allowed(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        var audit = SeccompProgram.AuditArch(arch);
        var clone = (uint)(arch == SeccompArch.X64 ? _x64Clone : _arm64Clone);
        const ulong threadFlags = 0x003D0F00;
        Assert.Equal(SeccompProgram.RetAllow, Verdict(program, audit, clone, threadFlags));
        foreach (var flag in new ulong[] { 0x00020000, 0x02000000, 0x04000000, 0x08000000, 0x10000000, 0x20000000, 0x40000000, 0x80 })
        {
            Assert.True(IsKill(Verdict(program, audit, clone, threadFlags | flag)), $"flag 0x{flag:X}");
            Assert.True(IsKill(Verdict(program, audit, clone, flag)), $"flag alone 0x{flag:X}");
        }

        Assert.NotEqual(SeccompProgram.RetAllow, Verdict(program, audit, 435, 0));
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Hostile_ioctl_and_prctl_commands_are_killed(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        var audit = SeccompProgram.AuditArch(arch);
        var ioctl = (uint)(arch == SeccompArch.X64 ? _x64Ioctl : _arm64Ioctl);
        var prctl = (uint)(arch == SeccompArch.X64 ? _x64Prctl : _arm64Prctl);

        // TIOCSTI, TIOCLINUX, TIOCSCTTY, TIOCCONS, TIOCSETD, FICLONE, FS_IOC_SETFLAGS, SIOCGIFCONF, TIOCSWINSZ, TCSETS
        foreach (var cmd in new ulong[] { 0x5412, 0x541C, 0x540E, 0x541D, 0x5423, 0x40049409, 0x40086602, 0x8912, 0x5414, 0x5402 })
        {
            Assert.True(IsKill(Verdict(program, audit, ioctl, 1, cmd)), $"ioctl 0x{cmd:X}");
        }

        // PR_SET_SECCOMP, PR_CAP_AMBIENT, PR_SET_MM, PR_SET_PTRACER, PR_SET_DUMPABLE, PR_SET_CHILD_SUBREAPER, PR_SET_SECUREBITS,
        // PR_CAPBSET_DROP, PR_SET_KEEPCAPS, PR_SET_PDEATHSIG, PR_SET_TSC, PR_SET_SPECULATION_CTRL
        foreach (var option in new ulong[] { 22, 47, 35, 0x59616d61, 4, 36, 28, 24, 8, 1, 26, 53 })
        {
            Assert.True(IsKill(Verdict(program, audit, prctl, option)), $"prctl {option}");
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Socket_is_killed_for_every_address_family(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        var audit = SeccompProgram.AuditArch(arch);
        var socket = (uint)(arch == SeccompArch.X64 ? 41 : 198);
        // AF_UNIX, AF_INET, AF_INET6, AF_NETLINK, AF_PACKET, AF_VSOCK, AF_BLUETOOTH, AF_ALG
        foreach (var family in new ulong[] { 1, 2, 10, 16, 17, 40, 31, 38 })
        {
            Assert.True(IsKill(Verdict(program, audit, socket, family)), $"family {family}");
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void No_new_privs_is_the_helpers_first_hardening_step_in_program_order(SeccompArch arch)
    {
        // Static check on Program.cs: SetNoNewPrivs is called before the Landlock ruleset and before the filter is installed.
        var source = File.ReadAllText(FindSource("Program.cs"));
        var nnp = source.IndexOf("Hardening.SetNoNewPrivs()", StringComparison.Ordinal);
        var landlock = source.IndexOf("Hardening.CreateLandlockRuleset(", StringComparison.Ordinal);
        var restrict = source.IndexOf("Hardening.RestrictSelf(", StringComparison.Ordinal);
        var seccomp = source.IndexOf("Hardening.InstallSeccomp(", StringComparison.Ordinal);
        var execve = source.IndexOf("Native.Execve(", StringComparison.Ordinal);
        Assert.True(nnp > 0 && nnp < landlock && landlock < restrict && restrict < seccomp && seccomp < execve, "hardening order is wrong in Program.cs");
        Assert.Contains("_seccompFilterFlagTsync = 1", File.ReadAllText(FindSource("Hardening.cs")), StringComparison.Ordinal);
        Assert.True(Enum.IsDefined(arch));
    }

    private static string FindSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Bazaar.Server.DevSandbox.Linux.Helper")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Bazaar.Server.DevSandbox.Linux.Helper", file);
    }
}
