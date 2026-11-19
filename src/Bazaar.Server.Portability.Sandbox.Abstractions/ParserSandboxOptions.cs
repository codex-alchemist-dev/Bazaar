namespace Bazaar.Server.Portability.Sandbox;

/// <summary>
/// Settings of the parser sandbox. Doc 44 section 8.3 names the limits but gives no numbers for the timeouts, the
/// memory and CPU ceilings or the output cap, so each is a setting with a safe default (configurability rule,
/// DECISIONS-LOG round 13). Operators may tighten them; the registry wiring (doc 03) is not built yet.
/// </summary>
public sealed record ParserSandboxOptions
{
    /// <summary>Default memory ceiling for the child, 2 GiB.</summary>
    public const long DefaultMaxMemoryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Default CPU-time ceiling for the child, 300 seconds.</summary>
    public const int DefaultMaxCpuSeconds = 300;

    /// <summary>Default cap on all bytes the child may write back for one job, 256 MiB.</summary>
    public const long DefaultMaxOutputBytes = 256L * 1024 * 1024;

    /// <summary>Default number of abnormal child endings that open the crash-loop circuit.</summary>
    public const int DefaultCrashLoopThreshold = 3;

    /// <summary>Default wall-clock allowance between two frames from the child, 30 seconds.</summary>
    public static TimeSpan DefaultFrameGapTimeout => TimeSpan.FromSeconds(30);

    /// <summary>Default wall-clock allowance for a whole detect or parse job, 15 minutes.</summary>
    public static TimeSpan DefaultJobTimeout => TimeSpan.FromMinutes(15);

    /// <summary>Default window in which abnormal endings are counted, 10 minutes.</summary>
    public static TimeSpan DefaultCrashLoopWindow => TimeSpan.FromMinutes(10);

    /// <summary>Path of the dotnet host used to start the parser host assembly. Default: <c>dotnet</c> on the path.</summary>
    public string DotnetPath { get; init; } = "dotnet";

    /// <summary>Path of <c>Bazaar.Server.Portability.ParserHost.dll</c>. Empty means the copy next to the sandbox assembly.</summary>
    public string HostAssemblyPath { get; init; } = string.Empty;

    /// <summary>Extra importer assemblies the child loads (importer modules). They load in the child, never in the server.</summary>
    public IReadOnlyList<string> ImporterAssemblies { get; init; } = [];

    /// <summary>Size and shape limits the child enforces.</summary>
    public ImportLimits Limits { get; init; } = ImportLimits.Default;

    /// <summary>
    /// Wall-clock allowance between two frames from the child, after the first. This is not a per-record parse timeout:
    /// the importer contract returns a materialized list, so the whole parse happens before the first frame, and that
    /// wait is bounded by <see cref="JobTimeout"/> only.
    /// </summary>
    public TimeSpan FrameGapTimeout { get; init; } = DefaultFrameGapTimeout;

    /// <summary>Wall-clock allowance for the whole job.</summary>
    public TimeSpan JobTimeout { get; init; } = DefaultJobTimeout;

    /// <summary>Memory ceiling for the child.</summary>
    public long MaxMemoryBytes { get; init; } = DefaultMaxMemoryBytes;

    /// <summary>CPU-time ceiling for the child.</summary>
    public int MaxCpuSeconds { get; init; } = DefaultMaxCpuSeconds;

    /// <summary>Cap on the total bytes the child may write back in one job.</summary>
    public long MaxOutputBytes { get; init; } = DefaultMaxOutputBytes;

    /// <summary>
    /// When true, a job fails before it starts if the OS provider cannot apply every confinement item. The default is
    /// false (best effort) so a host whose OS lacks an item still imports; the applied and missing items are reported
    /// through <see cref="ParserRunInfo"/>.
    /// </summary>
    public bool RequireFullConfinement { get; init; }

    /// <summary>Abnormal endings of one importer's child within <see cref="CrashLoopWindow"/> that raise <c>parser_crash_loop</c>.</summary>
    public int CrashLoopThreshold { get; init; } = DefaultCrashLoopThreshold;

    /// <summary>Window for <see cref="CrashLoopThreshold"/>. While the circuit is open, jobs for that importer are refused.</summary>
    public TimeSpan CrashLoopWindow { get; init; } = DefaultCrashLoopWindow;

    /// <summary>Checks the values. Throws for a non-positive timeout, a memory ceiling under 64 MiB, or a count under one.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FrameGapTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(JobTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMemoryBytes, 64L * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCpuSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOutputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CrashLoopThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CrashLoopWindow, TimeSpan.Zero);
    }
}

/// <summary>What the confinement provider did for a child, for the preview and for diagnostics.</summary>
public sealed record ParserRunInfo(string Provider, IReadOnlyList<string> Applied, IReadOnlyList<string> NotApplied);
