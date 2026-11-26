namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests;

public sealed class SeccompProgramTests
{
    public static TheoryData<SeccompArch> Arches => new() { SeccompArch.X64, SeccompArch.Arm64 };

    /// <summary>A minimal classic-BPF interpreter for the opcodes the filter uses (ld abs, jeq k, jset k, ret k), reading a seccomp_data image.</summary>
    private static uint Evaluate(BpfInstruction[] program, uint arch, uint nr, ulong arg0 = 0, ulong arg1 = 0)
    {
        var data = new byte[64];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), nr);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), arch);
        BitConverter.TryWriteBytes(data.AsSpan(16, 8), arg0);
        BitConverter.TryWriteBytes(data.AsSpan(24, 8), arg1);
        uint a = 0;
        var pc = 0;
        for (var steps = 0; steps < 10000; steps++)
        {
            Assert.InRange(pc, 0, program.Length - 1);
            var i = program[pc];
            switch (i.Code)
            {
                case SeccompProgram.LdWAbs:
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

    private static uint Run(SeccompArch arch, string syscall, ulong arg0 = 0, ulong arg1 = 0, string[]? extra = null)
    {
        Assert.True(SyscallTable.TryGetNumber(syscall, arch, out var nr), syscall + " has no number on " + arch);
        return Evaluate(SeccompProgram.Build(arch, extra ?? []), SeccompProgram.AuditArch(arch), (uint)nr, arg0, arg1);
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void The_architecture_check_comes_first_and_kills_on_mismatch(SeccompArch arch)
    {
        var p = SeccompProgram.Build(arch, []);

        Assert.Equal(SeccompProgram.LdWAbs, p[0].Code);
        Assert.Equal(4u, p[0].K);
        Assert.Equal(SeccompProgram.JeqK, p[1].Code);
        Assert.Equal(SeccompProgram.AuditArch(arch), p[1].K);
        Assert.Equal(1, p[1].Jt);
        Assert.Equal(0, p[1].Jf);
        Assert.Equal(SeccompProgram.RetK, p[2].Code);
        Assert.Equal(SeccompProgram.RetKillProcess, p[2].K);
        Assert.Equal(SeccompProgram.LdWAbs, p[3].Code);
        Assert.Equal(0u, p[3].K);
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void The_default_action_is_kill_process(SeccompArch arch)
    {
        var p = SeccompProgram.Build(arch, []);
        var last = p[^1];

        Assert.Equal(SeccompProgram.RetK, last.Code);
        Assert.Equal(SeccompProgram.RetKillProcess, last.K);
        Assert.Equal(0x80000000u, SeccompProgram.RetKillProcess);
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void A_foreign_architecture_and_the_x32_abi_are_killed_even_for_allowed_numbers(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        SyscallTable.TryGetNumber("read", arch, out var read);
        var other = arch == SeccompArch.X64 ? SeccompProgram.AuditArchAarch64 : SeccompProgram.AuditArchX86_64;

        Assert.Equal(SeccompProgram.RetKillProcess, Evaluate(program, other, (uint)read));
        Assert.Equal(SeccompProgram.RetKillProcess, Evaluate(program, 0, (uint)read));
        Assert.Equal(SeccompProgram.RetKillProcess, Evaluate(program, SeccompProgram.AuditArch(arch), 0x40000000u | (uint)read));
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void The_allow_list_is_ordered_after_the_explicit_denials_and_every_instruction_is_well_formed(SeccompArch arch)
    {
        var p = SeccompProgram.Build(arch, []);

        SyscallTable.TryGetNumber("ptrace", arch, out var ptrace);
        SyscallTable.TryGetNumber("read", arch, out var read);
        var ptraceIndex = Array.FindIndex(p, i => i.Code == SeccompProgram.JeqK && i.K == (uint)ptrace);
        var readIndex = Array.FindIndex(p, i => i.Code == SeccompProgram.JeqK && i.K == (uint)read);
        Assert.InRange(ptraceIndex, 4, readIndex - 1);
        Assert.Equal(SeccompProgram.RetKillProcess, p[ptraceIndex + 1].K);
        Assert.Equal(SeccompProgram.RetAllow, p[readIndex + 1].K);

        Assert.InRange(p.Length, 10, 4096);
        for (var i = 0; i < p.Length; i++)
        {
            Assert.Contains(p[i].Code, new[] { SeccompProgram.LdWAbs, SeccompProgram.JeqK, SeccompProgram.JsetK, SeccompProgram.RetK });
            if (p[i].Code is SeccompProgram.JeqK or SeccompProgram.JsetK)
            {
                Assert.True(i + 1 + p[i].Jt < p.Length && i + 1 + p[i].Jf < p.Length, $"jump out of range at {i}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Every_base_syscall_that_exists_on_the_architecture_is_allowed(SeccompArch arch)
    {
        var program = SeccompProgram.Build(arch, []);
        foreach (var name in SeccompProgram.BaseAllow)
        {
            Assert.True(SyscallTable.IsKnown(name), name + " is not in the syscall table");
            if (!SyscallTable.TryGetNumber(name, arch, out var nr))
            {
                continue;
            }

            if (name is "ioctl" or "prctl" or "clone")
            {
                continue;
            }

            Assert.Equal(SeccompProgram.RetAllow, Evaluate(program, SeccompProgram.AuditArch(arch), (uint)nr));
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Every_denied_syscall_that_exists_on_the_architecture_is_killed(SeccompArch arch)
    {
        foreach (var name in SeccompProgram.Denied)
        {
            Assert.True(SyscallTable.IsKnown(name), name + " is not in the syscall table");
            if (SyscallTable.TryGetNumber(name, arch, out _))
            {
                Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, name));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void The_dangerous_syscalls_named_by_the_decision_are_killed(SeccompArch arch)
    {
        foreach (var name in new[]
        {
            "ptrace", "mount", "umount2", "pivot_root", "unshare", "setns", "bpf", "perf_event_open", "keyctl", "kexec_load",
            "init_module", "finit_module", "socket", "connect", "bind", "open_by_handle_at", "process_vm_readv", "process_vm_writev", "userfaultfd",
        })
        {
            Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, name));
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void A_syscall_on_no_list_is_killed_by_the_default(SeccompArch arch)
    {
        // setuid-family and sethostname-like calls are on neither list.
        Assert.Equal(SeccompProgram.RetKillProcess, Evaluate(SeccompProgram.Build(arch, []), SeccompProgram.AuditArch(arch), 9999));
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Optional_syscalls_are_allowed_only_when_the_policy_adds_them(SeccompArch arch)
    {
        Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, "memfd_create"));
        Assert.Equal(SeccompProgram.RetAllow, Run(arch, "memfd_create", extra: ["memfd_create"]));
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Clone_is_allowed_for_threads_and_processes_but_not_with_a_namespace_flag(SeccompArch arch)
    {
        const ulong threadFlags = 0x003D0F00; // CLONE_VM|FS|FILES|SIGHAND|THREAD|SYSVSEM|SETTLS|PARENT_SETTID|CHILD_CLEARTID
        Assert.Equal(SeccompProgram.RetAllow, Run(arch, "clone", threadFlags));
        Assert.Equal(SeccompProgram.RetAllow, Run(arch, "clone", 0x01200011));
        foreach (var flag in new ulong[] { 0x10000000, 0x40000000, 0x20000000, 0x00020000, 0x04000000, 0x08000000, 0x02000000, 0x80 })
        {
            Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, "clone", threadFlags | flag));
        }
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Clone3_answers_enosys_so_libc_falls_back_to_clone(SeccompArch arch)
    {
        Assert.Equal(SeccompProgram.RetErrnoBase | SeccompProgram.Enosys, Run(arch, "clone3"));
        Assert.Equal(0x00050026u, SeccompProgram.RetErrnoBase | SeccompProgram.Enosys);
    }

    [Theory]
    [MemberData(nameof(Arches))]
    public void Ioctl_and_prctl_are_allowed_only_for_the_listed_commands(SeccompArch arch)
    {
        foreach (var cmd in SeccompProgram.IoctlAllowed)
        {
            Assert.Equal(SeccompProgram.RetAllow, Run(arch, "ioctl", 1, cmd));
        }

        Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, "ioctl", 1, 0x5412)); // TIOCSTI
        Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, "ioctl", 1, 0x40049409)); // FS_IOC_ADD_ENCRYPTION_KEY-like

        foreach (var option in SeccompProgram.PrctlAllowed)
        {
            Assert.Equal(SeccompProgram.RetAllow, Run(arch, "prctl", option));
        }

        Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, "prctl", 22)); // PR_SET_SECCOMP
        Assert.Equal(SeccompProgram.RetKillProcess, Run(arch, "prctl", 8)); // PR_SET_KEEPCAPS
    }

    [Fact]
    public void Syscall_numbers_match_the_kernel_for_a_known_sample()
    {
        Assert.True(SyscallTable.TryGetNumber("read", SeccompArch.X64, out var n));
        Assert.Equal(0, n);
        Assert.True(SyscallTable.TryGetNumber("socket", SeccompArch.X64, out n));
        Assert.Equal(41, n);
        Assert.True(SyscallTable.TryGetNumber("execve", SeccompArch.X64, out n));
        Assert.Equal(59, n);
        Assert.True(SyscallTable.TryGetNumber("seccomp", SeccompArch.X64, out n));
        Assert.Equal(317, n);
        Assert.True(SyscallTable.TryGetNumber("read", SeccompArch.Arm64, out n));
        Assert.Equal(63, n);
        Assert.True(SyscallTable.TryGetNumber("socket", SeccompArch.Arm64, out n));
        Assert.Equal(198, n);
        Assert.True(SyscallTable.TryGetNumber("execve", SeccompArch.Arm64, out n));
        Assert.Equal(221, n);
        Assert.True(SyscallTable.TryGetNumber("seccomp", SeccompArch.Arm64, out n));
        Assert.Equal(277, n);
        Assert.False(SyscallTable.TryGetNumber("open", SeccompArch.Arm64, out _));
        Assert.False(SyscallTable.TryGetNumber("no_such_syscall", SeccompArch.X64, out _));
    }

    [Fact]
    public void The_lists_do_not_overlap()
    {
        Assert.Empty(SeccompProgram.Denied.Intersect(SeccompProgram.BaseAllow));
        Assert.Empty(SeccompProgram.Denied.Intersect(SeccompProgram.Optional));
        Assert.Empty(SeccompProgram.BaseAllow.Intersect(SeccompProgram.Optional));
        foreach (var name in SeccompProgram.Optional)
        {
            Assert.True(SyscallTable.IsKnown(name), name);
        }
    }

    [Fact]
    public void The_running_architecture_is_detected()
    {
        var detected = SeccompProgram.DetectArch();
        if (RuntimeInformationIs(System.Runtime.InteropServices.Architecture.X64))
        {
            Assert.Equal(SeccompArch.X64, detected);
        }
        else if (RuntimeInformationIs(System.Runtime.InteropServices.Architecture.Arm64))
        {
            Assert.Equal(SeccompArch.Arm64, detected);
        }
        else
        {
            Assert.Null(detected);
        }
    }

    private static bool RuntimeInformationIs(System.Runtime.InteropServices.Architecture a) =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == a;
}
