using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>Creates the real Win32 implementation, only on Windows.</summary>
internal static class WindowsProvider
{
    internal static IWindowsConfinementApi? CreateRealApi() => OperatingSystem.IsWindows() ? CreateWin32() : null;

    [SupportedOSPlatform("windows")]
    private static Win32ConfinementApi CreateWin32() => new();
}

/// <summary>
/// Windows confinement provider (doc 48 section 2.1, "Windows provider (PROPOSED)"). Starts the module as a process created
/// suspended in an AppContainer with no capabilities (so no network and no file access outside the per-run work directory), assigned
/// to a job object (kill-on-close, active-process, memory and CPU-time limits, all UI restrictions) before it runs, with a scrubbed
/// environment, a closed stdin and an empty per-run work directory whose access list names only the current user, SYSTEM and the run's
/// AppContainer. <see cref="Tested"/> is true only when <see cref="WindowsConfinementSelfTest"/> proved the job, file and network
/// properties on this host; otherwise <see cref="UntestedReason"/> says why and nothing starts. There is no unconfined fallback, and
/// the restricted-token fallback never makes the provider tested; <see cref="HardeningLevel"/> reports what was proved.
/// </summary>
public sealed partial class WindowsConfinementProvider : ISandboxConfinementProvider
{
    private readonly WindowsConfinementOptions _options;
    private readonly IWindowsSandboxLaunchPlan _plan;
    private readonly IWindowsConfinementApi? _api;
    private readonly Lazy<WindowsConfinementSelfTestResult> _selfTest;

    /// <summary>Creates the provider. The self-test runs lazily, once, the first time <see cref="Tested"/> is read.</summary>
    public WindowsConfinementProvider(WindowsConfinementOptions options, IWindowsSandboxLaunchPlan plan)
        : this(options, plan, WindowsProvider.CreateRealApi())
    {
    }

    internal WindowsConfinementProvider(WindowsConfinementOptions options, IWindowsSandboxLaunchPlan plan, IWindowsConfinementApi? api)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);
        _options = options;
        _plan = plan;
        _api = api;
        _selfTest = new Lazy<WindowsConfinementSelfTestResult>(
            () => WindowsConfinementSelfTest.RunAsync(options, api, CancellationToken.None).GetAwaiter().GetResult(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public SandboxOs Os => SandboxOs.Windows;

    /// <inheritdoc />
    public bool Tested => _selfTest.Value.Passed;

    /// <summary>Why <see cref="Tested"/> is false, or null when it is true.</summary>
    public string? UntestedReason => _selfTest.Value.Summary;

    /// <summary>What the self-test proved: <c>none</c>, <c>job+restricted-token</c> or <c>job+appcontainer</c>.</summary>
    public string HardeningLevel => _selfTest.Value.HardeningLevel;

    /// <summary>The cached self-test result.</summary>
    public WindowsConfinementSelfTestResult SelfTestResult => _selfTest.Value;

    /// <inheritdoc />
    public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();
        if (!Tested || _api is null)
        {
            throw new InvalidOperationException($"Windows confinement is not tested on this host: {UntestedReason}");
        }

        if (!RunIdPattern().IsMatch(spec.RunId))
        {
            throw new ArgumentException("RunId must be lowercase letters, digits and hyphens.", nameof(spec));
        }

        if (spec.MemoryLimitMb <= 0 || spec.TimeoutSeconds <= 0)
        {
            throw new ArgumentException("MemoryLimitMb and TimeoutSeconds must be positive.", nameof(spec));
        }

        var process = WindowsLauncher.Launch(
            _api,
            _options,
            spec.RunId,
            WindowsLauncher.JobSpecFor(spec, _options),
            WindowsConfinementMode.AppContainer,
            work => _plan.Create(spec, work),
            allowShell: false);
        return Task.FromResult<ISandboxProcess>(process);
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex RunIdPattern();
}
