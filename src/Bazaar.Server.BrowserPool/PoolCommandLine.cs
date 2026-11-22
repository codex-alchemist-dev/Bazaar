using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool;

// Verified on an Ubuntu 24.04 container (kernel 6.18, root, bubblewrap 0.9.0, util-linux setpriv, Chromium 153), date 2026-11-19:
//  - setpriv --reuid U --regid U --clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all, then bwrap as that uid, then
//    Chromium: all three start. bwrap is NOT setuid here; it uses an unprivileged user namespace, which is why --no-new-privs
//    (that blocks setuid helpers) is wanted and harmless. Dropping the bounding set did NOT stop bwrap or Chromium from creating
//    their own user namespaces (creating one needs no capability), so it stays.
//  - Chromium's own sandbox stays ON (no --no-sandbox anywhere): renderer, network-service and utility processes report
//    Seccomp: 2 in /proc/<pid>/status. The GPU process reports Seccomp: 0 (software rendering via --disable-gpu); it still sits inside
//    the bwrap sandbox and the slot uid.
//  - Inside the sandbox /root and /var do not exist, /home holds only the empty profile mount, and id -u prints the slot uid.
//  - A host with kernel.apparmor_restrict_unprivileged_userns=1 (stock Ubuntu 24.04 desktop and server) needs an AppArmor profile
//    that lets bwrap create user namespaces; the container used for this check had no such sysctl. If user namespaces are denied,
//    bwrap fails and the open fails closed (LaunchFailed). The fix is never --no-sandbox.
//  - The real-process checks are in tests/Bazaar.Server.BrowserPool.Tests/RealSandboxTests.cs.
//
// Network, default NetworkIsolation.PerSlotNamespace: each slot gets its own network namespace (bzpool-N) joined to the VM by a
// veth pair (bzvN in the VM at the first address of a /30, bzpN in the namespace at the second, no default route). The launch
// enters it with nsenter BEFORE the privilege drop (entering needs CAP_SYS_ADMIN, which the drop removes); bwrap still does not
// unshare the net namespace, so it inherits the slot's. NftablesRules DNATs "veth address : egress port" to the real endpoint
// and drops everything else that arrives from any bzv* veth, in input and forward; the host applies that document
// (IPoolVmHost.ApplyFirewallAsync) after the namespace exists and before the launch. The per-uid output rule is kept as a second
// layer, but nft tables are per namespace, so it only sees a slot-uid process in the VM's own namespace, not the slot's traffic.
// Verified for real on this container (Ubuntu 24.04, kernel 6.18, root, iproute2 6.1, nftables, nsenter, bubblewrap 0.9.0,
// Chromium 153), date 2026-11-19, in tests/Bazaar.Server.BrowserPool.Tests/NetnsRealTests.cs and RealSandboxTests.cs:
//  - from inside the sandbox of a slot, TCP to the veth address and the proxy port reaches a stand-in on the VM's loopback
//    (route_localnet scoped to that veth) and on another namespace acting as a remote host (forwarding scoped to that veth and to
//    the named egress interface, plus masquerade; the remote saw the VM's address, not the slot's);
//  - another port on the veth address, a non-loopback address of the VM, 127.0.0.1 and the proxy's own address are all unreachable,
//    and the control (the same listener before the rules) was reachable, so the probe tells the two apart;
//  - an abstract UNIX socket listening in the VM's namespace is connectable in SharedWithUidRule mode and NOT from a slot's namespace;
//  - two slots cannot reach each other (each reaches its own proxy; a listener inside one slot is unreachable from the other);
//  - after the cleanup commands the namespace file, the veth and the slot's rules are gone, cleanup is idempotent, and a
//    half-made or stale namespace is removed or replaced;
//  - real Chromium runs in per-slot mode, its browser process is in the slot's namespace and no slot process is in the VM's, and the
//    proxy stand-in receives the request for example.invalid through the veth and the DNAT.
// Not verified: a host with a kernel that lacks nf_tables, veth or user namespaces (the tests skip with the reason there); a
// real Hyper-V or QEMU pool VM and its own NIC (only a second namespace stood in for a remote proxy host); IPv6 (the inet drop
// covers it, but no IPv6 probe was run); a netns-creating escape from inside Chromium's sandbox (out of scope of this check).
//
// Network, NetworkIsolation.SharedWithUidRule: the sandbox shares the VM's network namespace (bwrap does too unless given
// --unshare-net). Confinement to the proxy is enforced only by the per-uid nftables rule. Abstract UNIX sockets, netlink state and
// loopback are shared with every other process in the VM; kept for hosts that cannot give the pool CAP_NET_ADMIN.

/// <summary>
/// Builds every argv list the pool runs in the pool VM. It is pure: same input, same output, no I/O. Nothing here goes through a
/// shell. The values that reach the lists are the validated options, an integer slot, an integer uid, an IPv4 literal and an
/// integer port, plus the URL after <see cref="Uri"/> normalisation as the last element.
/// </summary>
public static class PoolCommandLine
{
    /// <summary>Where the slot's profile is mounted inside the sandbox.</summary>
    public const string SandboxHome = "/home/browser";

    /// <summary>Where the slot's runtime directory is mounted inside the sandbox.</summary>
    public const string SandboxRuntimeDir = "/run/user/browser";

    /// <summary>The uid (and gid) of a slot.</summary>
    public static int SlotUid(BrowserPoolOptions options, int slot)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        return options.SlotUidBase + slot;
    }

    /// <summary>The slot's directory on the pool VM, built only from the validated slot root and the integer slot.</summary>
    public static string SlotDirectory(BrowserPoolOptions options, int slot)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        var dir = options.SlotRoot + "/slot-" + PoolRequestValidator.Invariant(slot);
        if (dir.Contains("..", StringComparison.Ordinal) || dir.Any(char.IsWhiteSpace) || dir.EndsWith('/'))
        {
            throw new ArgumentException("The slot directory is not acceptable.", nameof(options));
        }

        return dir;
    }

    /// <summary>Builds the whole spec for one session. Throws <see cref="ArgumentException"/> on anything that fails validation.</summary>
    public static PoolLaunchSpec Build(
        BrowserPoolOptions options,
        string sessionId,
        int slot,
        PoolSessionRequest request,
        PoolEgressEndpoint egress)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var requestError = PoolRequestValidator.Validate(request, out var startUrl);
        if (requestError is not null)
        {
            throw new ArgumentException(requestError, nameof(request));
        }

        var endpointError = PoolRequestValidator.ValidateEndpoint(egress);
        if (endpointError is not null)
        {
            throw new ArgumentException(endpointError, nameof(egress));
        }

        var uid = SlotUid(options, slot);
        var dir = SlotDirectory(options, slot);
        var headed = request.Display == PoolDisplayMode.Headed;
        var perSlot = options.NetworkIsolation == NetworkIsolation.PerSlotNamespace;

        // In a per-slot namespace the browser's proxy is the VM-side veth address; the firewall DNATs it to the real endpoint.
        var browserEgress = egress;
        var prepare = new List<PoolCommand>(PrepareCommands(options, slot));
        if (perSlot)
        {
            if (PoolNetwork.IsInsideRange(options, egress.Host))
            {
                throw new ArgumentException("The egress proxy may not sit inside the slot network range.", nameof(egress));
            }

            browserEgress = new PoolEgressEndpoint(PoolNetwork.HostAddress(options, slot), egress.Port);
            prepare.AddRange(PoolNetwork.SetupCommands(options, slot, egress));
        }

        var launch = new List<string>();
        if (options.UseSystemdRun)
        {
            launch.AddRange(SystemdRunPrefix(options));
        }

        if (perSlot)
        {
            // Enter the namespace while still root; the privilege drop that follows would remove the right to.
            launch.AddRange(PoolNetwork.EnterPrefix(options, slot));
        }

        launch.AddRange(SandboxPrefix(options, slot, headed));
        if (headed)
        {
            launch.Add(options.CagePath);
            launch.Add("--");
        }

        launch.AddRange(ChromiumArgs(options, request, browserEgress, startUrl, headed));

        return new PoolLaunchSpec(
            sessionId,
            request.AgentId,
            slot,
            uid,
            prepare,
            launch,
            CleanupCommands(options, slot),
            new PoolFirewallRule(uid, egress.Host, egress.Port));
    }

    /// <summary>Commands that give the slot an empty, private directory tree owned by the slot user, on plain disk (no size cap).</summary>
    public static IReadOnlyList<PoolCommand> PrepareCommands(int uid, string slotDir)
    {
        var u = PoolRequestValidator.Invariant(uid);
        return
        [
            new PoolCommand(["rm", "-rf", "--one-file-system", slotDir]),
            new PoolCommand(["install", "-d", "-m", "0700", "-o", u, "-g", u, slotDir]),
            new PoolCommand(["install", "-d", "-m", "0700", "-o", u, "-g", u, slotDir + "/profile"]),
            new PoolCommand(["install", "-d", "-m", "0700", "-o", u, "-g", u, slotDir + "/run"]),
        ];
    }

    /// <summary>
    /// The prepare for a slot under the configured limits. With <see cref="BrowserPoolOptions.DiskMaxMb"/> at 0 it is exactly
    /// <see cref="PrepareCommands(int, string)"/>. Otherwise the slot directory becomes a size-limited tmpfs:
    /// <list type="number">
    /// <item>unmount whatever a crashed session left (exit 32 means nothing was mounted, which is fine);</item>
    /// <item><c>rm -rf --one-file-system</c>: if a mount is still there (busy, or stacked) the final <c>rmdir</c> fails with EBUSY, so the
    /// open fails instead of running on a stale tmpfs; it never crosses into another file system;</item>
    /// <item>create the mount point, mount the tmpfs, and only then create <c>profile</c> and <c>run</c> inside it.</item>
    /// </list>
    /// The tmpfs is <c>nosuid,nodev,noexec</c>: Chromium keeps no executable code in its profile or runtime directory (the real-browser
    /// tests in this repo run with it on), and a slot user that writes a binary there cannot run it.
    /// </summary>
    public static IReadOnlyList<PoolCommand> PrepareCommands(BrowserPoolOptions options, int slot)
    {
        var uid = SlotUid(options, slot);
        var dir = SlotDirectory(options, slot);
        var plain = PrepareCommands(uid, dir);
        if (options.DiskMaxMb == 0)
        {
            return plain;
        }

        // plain is [rm, install slot, install profile, install run].
        return
        [
            UnmountCommand(options, dir),
            plain[0],
            plain[1],
            new PoolCommand(
            [
                options.MountPath,
                "-t", "tmpfs",
                "-o", "size=" + PoolRequestValidator.Invariant(options.DiskMaxMb) + "m"
                    + ",nr_inodes=" + PoolRequestValidator.Invariant(options.DiskMaxInodes)
                    + ",mode=0711,nosuid,nodev,noexec",
                "tmpfs",
                dir,
            ]),
            plain[2],
            plain[3],
        ];
    }

    // umount exits 32 both for "not mounted" and "busy". The caller's next step, rm -rf --one-file-system on the same path,
    // tells them apart: it fails with EBUSY when a mount is still there.
    private static PoolCommand UnmountCommand(BrowserPoolOptions options, string slotDir) =>
        new([options.UmountPath, slotDir], [0, 32]);

    /// <summary>Commands that run at close, always: kill everything the slot user owns, then wipe the directory.</summary>
    public static IReadOnlyList<PoolCommand> CleanupCommands(int uid, string slotDir) =>
    [
        // pkill exits 1 when nothing matched, which is the normal case for a browser that already exited.
        new PoolCommand(["pkill", "-KILL", "-U", PoolRequestValidator.Invariant(uid)], [0, 1]),
        new PoolCommand(["rm", "-rf", "--one-file-system", slotDir]),
    ];

    /// <summary>
    /// The cleanup for a slot under the configured isolation: kill the slot user's processes, remove the slot's namespace and veth
    /// (per-slot mode, after the kill so nothing is left to use them), unmount the slot tmpfs when a disk cap is on, then wipe the directory. It is the same list for a session
    /// that failed half-way, so a namespace made by a failed open is removed too.
    /// </summary>
    public static IReadOnlyList<PoolCommand> CleanupCommands(BrowserPoolOptions options, int slot)
    {
        var uid = SlotUid(options, slot);
        var dir = SlotDirectory(options, slot);
        var baseline = CleanupCommands(uid, dir);
        var network = options.NetworkIsolation == NetworkIsolation.PerSlotNamespace
            ? PoolNetwork.CleanupCommands(options, slot)
            : [];
        if (options.DiskMaxMb == 0)
        {
            // baseline is [pkill, rm]: the network goes between them.
            return [baseline[0], .. network, baseline[1]];
        }

        // Unmount after the kill (nothing is left holding the mount), then rm: it removes what is left and, because rmdir of a
        // mount point fails with EBUSY, fails the cleanup when the unmount did not take. A lazy unmount is never used, since it
        // would hide a busy mount. Not mounted, or a missing directory, is fine: umount exits 32 and rm -rf of nothing is 0.
        return [baseline[0], .. network, UnmountCommand(options, dir), baseline[1]];
    }

    /// <summary>
    /// The <c>systemd-run</c> scope prefix with the per-session limits: memory (<see cref="BrowserPoolOptions.MemoryMaxMb"/>), processor
    /// time (<see cref="BrowserPoolOptions.CpuQuotaPercent"/>) and task count (<see cref="BrowserPoolOptions.TasksMax"/>).
    /// </summary>
    public static IReadOnlyList<string> SystemdRunPrefix(BrowserPoolOptions options)
    {
        EnsureValid(options);
        var prefix = new List<string> { "systemd-run", "--scope", "--quiet", "--collect" };
        if (options.MemoryMaxMb > 0)
        {
            prefix.AddRange(["-p", "MemoryMax=" + PoolRequestValidator.Invariant(options.MemoryMaxMb) + "M", "-p", "MemorySwapMax=0"]);
        }

        if (options.CpuQuotaPercent > 0)
        {
            prefix.AddRange(["-p", "CPUQuota=" + PoolRequestValidator.Invariant(options.CpuQuotaPercent) + "%"]);
        }

        prefix.AddRange(["-p", "TasksMax=" + PoolRequestValidator.Invariant(options.TasksMax), "--"]);
        return prefix;
    }

    /// <summary>
    /// The privilege drop and the namespace sandbox, ending just before the program to run. Used for the browser and, by the real
    /// sandbox test, for a probe command, so both see the same confinement.
    /// </summary>
    public static IReadOnlyList<string> SandboxPrefix(BrowserPoolOptions options, int slot, bool headed)
    {
        EnsureValid(options);
        EnsureSlot(options, slot);
        var uid = PoolRequestValidator.Invariant(SlotUid(options, slot));
        var dir = SlotDirectory(options, slot);

        var argv = new List<string>
        {
            options.SetprivPath,
            "--reuid", uid,
            "--regid", uid,
            "--clear-groups",
            "--no-new-privs",
            "--inh-caps=-all",
            "--bounding-set=-all",
            "--",
            options.BwrapPath,
            "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup-try",
            "--die-with-parent", "--new-session", "--clearenv",
            "--hostname", "pool",
            "--setenv", "HOME", SandboxHome,
            "--setenv", "XDG_RUNTIME_DIR", SandboxRuntimeDir,
            "--setenv", "PATH", "/usr/bin:/bin",
            "--setenv", "LANG", "C.UTF-8",
        };

        if (headed)
        {
            argv.AddRange(["--setenv", "WLR_BACKENDS", "headless", "--setenv", "WLR_RENDERER", "pixman", "--setenv", "WLR_LIBINPUT_NO_DEVICES", "1"]);
        }

        // Read-only system files. Not bound: /etc/passwd, /etc/hosts, /etc/resolv.conf, /home, /root, /var, /run, any socket
        // directory, the slot root. There is no --unshare-net: see the note at the top of this file.
        argv.AddRange(["--ro-bind", "/usr", "/usr"]);
        argv.AddRange(["--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib64", "/lib64", "--symlink", "usr/sbin", "/sbin"]);
        foreach (var etc in new[] { "/etc/fonts", "/etc/ssl", "/etc/ca-certificates" })
        {
            argv.AddRange(["--ro-bind-try", etc, etc]);
        }

        foreach (var extra in options.ExtraReadOnlyBinds)
        {
            argv.AddRange(["--ro-bind", extra, extra]);
        }

        argv.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp"]);
        argv.AddRange(["--bind", dir + "/profile", SandboxHome, "--bind", dir + "/run", SandboxRuntimeDir]);
        argv.AddRange(["--chdir", SandboxHome]);
        return argv;
    }

    /// <summary>The Chromium command, program first. It never contains a sandbox-weakening flag, whatever the options.</summary>
    public static IReadOnlyList<string> ChromiumArgs(
        BrowserPoolOptions options,
        PoolSessionRequest request,
        PoolEgressEndpoint egress,
        string startUrl,
        bool headed)
    {
        var host = egress.Host;
        var hostPort = host + ":" + PoolRequestValidator.Invariant(egress.Port);
        var argv = new List<string>
        {
            options.ChromiumPath,
            "--user-data-dir=" + SandboxHome + "/profile",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-breakpad",
            "--password-store=basic",
            "--disable-features=Translate,MediaRouter",
            "--proxy-server=http://" + hostPort,
            "--proxy-bypass-list=<-loopback>",
            "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE " + host,
            "--remote-debugging-pipe",
            "--window-size=" + PoolRequestValidator.Invariant(request.Width) + "," + PoolRequestValidator.Invariant(request.Height),
            // One CSS pixel is one screenshot pixel, so a click at a screenshot coordinate lands where the agent saw it.
            "--force-device-scale-factor=1",
        };

        if (headed)
        {
            argv.Add("--ozone-platform=wayland");
        }
        else
        {
            argv.Add("--headless=new");
        }

        argv.Add("--disable-gpu");

        // Chromium stops reading switches at "--" (base::CommandLine), so even a URL that began with '-' could not become a flag.
        // Validation also refuses such a URL, and only the normalised absolute URI is passed.
        argv.Add("--");
        argv.Add(startUrl);
        return argv;
    }

    private static void EnsureValid(BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = BrowserPoolOptionsValidator.Check(options);
        if (errors.Count != 0)
        {
            throw new ArgumentException("Invalid browser pool options: " + string.Join(' ', errors), nameof(options));
        }
    }

    private static void EnsureSlot(BrowserPoolOptions options, int slot)
    {
        if (slot < 0 || slot >= options.MaxSessions)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "The slot is outside the pool.");
        }
    }
}
