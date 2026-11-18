using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>
/// Linux confinement provider (doc 48 section 2.1, "Linux provider (PROPOSED)"). Starts the module through util-linux
/// <c>prlimit</c> wrapping <c>unshare</c> (user with root mapping, net, pid, mount, ipc and uts namespaces, <c>--fork --kill-child
/// --mount-proc</c>), with an empty environment except an explicit PATH, an empty private working directory, a closed stdin and
/// address-space, CPU-time, process-count and open-file limits.
/// <para>
/// This is namespaces plus rlimits only. It does NOT provide Landlock or seccomp: the module still sees the host file system
/// (same user, no pivot_root), can make any syscall the kernel permits an unprivileged user, and the memory limit is an
/// address-space limit, not a cgroup. Landlock and seccomp are a follow-up hardening step that needs a native helper, because .NET
/// cannot run code between fork and exec. <see cref="Tested"/> is true only when <see cref="LinuxConfinementSelfTest"/> proved the
/// namespace, network, pid and limit properties on this host; otherwise <see cref="UntestedReason"/> says why and no process starts.
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed partial class LinuxConfinementProvider : ISandboxConfinementProvider
{
    private readonly LinuxConfinementOptions _options;
    private readonly ILinuxSandboxLaunchPlan _plan;
    private readonly Lazy<LinuxConfinementSelfTestResult> _selfTest;

    /// <summary>Creates the provider. The self-test runs lazily, once, the first time <see cref="Tested"/> is read.</summary>
    public LinuxConfinementProvider(LinuxConfinementOptions options, ILinuxSandboxLaunchPlan plan)
        : this(options, plan, null)
    {
    }

    internal LinuxConfinementProvider(LinuxConfinementOptions options, ILinuxSandboxLaunchPlan plan, ILinuxProbeRunner? probeRunner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);
        _options = options;
        _plan = plan;
        _selfTest = new Lazy<LinuxConfinementSelfTestResult>(
            () => (probeRunner is null
                ? LinuxConfinementSelfTest.RunAsync(options)
                : LinuxConfinementSelfTest.RunAsync(options, probeRunner, CancellationToken.None)).GetAwaiter().GetResult(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public SandboxOs Os => SandboxOs.Linux;

    /// <inheritdoc />
    public bool Tested => _selfTest.Value.Passed;

    /// <summary>Why <see cref="Tested"/> is false, or null when it is true.</summary>
    public string? UntestedReason => _selfTest.Value.Summary;

    /// <summary>The cached self-test result.</summary>
    public LinuxConfinementSelfTestResult SelfTestResult => _selfTest.Value;

    /// <inheritdoc />
    public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();
        if (!Tested)
        {
            throw new InvalidOperationException($"Linux confinement is not tested on this host: {UntestedReason}");
        }

        if (!RunIdPattern().IsMatch(spec.RunId))
        {
            throw new ArgumentException("RunId must be lowercase letters, digits and hyphens.", nameof(spec));
        }

        if (spec.MemoryLimitMb <= 0 || spec.TimeoutSeconds <= 0)
        {
            throw new ArgumentException("MemoryLimitMb and TimeoutSeconds must be positive.", nameof(spec));
        }

        var unshare = _options.ResolveUnshare() ?? throw new InvalidOperationException("unshare not found");
        var prlimit = _options.ResolvePrlimit() ?? throw new InvalidOperationException("prlimit not found");

        var root = _options.WorkRoot ?? Path.GetTempPath();
        var workDir = Path.Combine(root, "bazaar-sbx-" + spec.RunId + "-" + Guid.NewGuid().ToString("N"));
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(workDir, OwnerOnly);
        try
        {
            File.SetUnixFileMode(workDir, OwnerOnly);
            var command = _plan.Create(spec, workDir);
            if (!Path.IsPathRooted(command.Executable) || !File.Exists(command.Executable))
            {
                throw new InvalidOperationException("The launch plan must give an existing absolute executable path.");
            }

            var args = LinuxCommandLine.BuildArguments(_options, unshare, spec.MemoryLimitMb, spec.TimeoutSeconds, command);
            var psi = LinuxProcessFactory.CreateStartInfo(_options, prlimit, args, workDir);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start.");
            process.StandardInput.Close();
            return Task.FromResult<ISandboxProcess>(new LinuxSandboxProcess(process, workDir));
        }
        catch
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            throw;
        }
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex RunIdPattern();
}
