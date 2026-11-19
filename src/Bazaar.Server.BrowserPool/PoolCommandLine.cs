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
// Network: the sandbox shares the VM's network namespace on purpose (the browser must reach its proxy; bwrap shares the net
// namespace unless --unshare-net is given, so no flag is needed). Confinement to the proxy is enforced outside this process by the
// per-uid nftables rule that NftablesRules renders and the host applies (IPoolVmHost.ApplyFirewallAsync) before the launch.

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

        var launch = new List<string>();
        if (options.UseSystemdRun)
        {
            launch.AddRange(SystemdRunPrefix(options));
        }

        launch.AddRange(SandboxPrefix(options, slot, headed));
        if (headed)
        {
            launch.Add(options.CagePath);
            launch.Add("--");
        }

        launch.AddRange(ChromiumArgs(options, request, egress, startUrl, headed));

        return new PoolLaunchSpec(
            sessionId,
            request.AgentId,
            slot,
            uid,
            PrepareCommands(uid, dir),
            launch,
            CleanupCommands(uid, dir),
            new PoolFirewallRule(uid, egress.Host, egress.Port));
    }

    /// <summary>Commands that give the slot an empty, private directory tree owned by the slot user.</summary>
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

    /// <summary>Commands that run at close, always: kill everything the slot user owns, then wipe the directory.</summary>
    public static IReadOnlyList<PoolCommand> CleanupCommands(int uid, string slotDir) =>
    [
        // pkill exits 1 when nothing matched, which is the normal case for a browser that already exited.
        new PoolCommand(["pkill", "-KILL", "-U", PoolRequestValidator.Invariant(uid)], [0, 1]),
        new PoolCommand(["rm", "-rf", "--one-file-system", slotDir]),
    ];

    /// <summary>The <c>systemd-run</c> scope prefix. Only the memory properties depend on <see cref="BrowserPoolOptions.MemoryMaxMb"/>.</summary>
    public static IReadOnlyList<string> SystemdRunPrefix(BrowserPoolOptions options)
    {
        EnsureValid(options);
        var prefix = new List<string> { "systemd-run", "--scope", "--quiet", "--collect" };
        if (options.MemoryMaxMb > 0)
        {
            prefix.AddRange(["-p", "MemoryMax=" + PoolRequestValidator.Invariant(options.MemoryMaxMb) + "M", "-p", "MemorySwapMax=0"]);
        }

        prefix.AddRange(["-p", "TasksMax=512", "--"]);
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
