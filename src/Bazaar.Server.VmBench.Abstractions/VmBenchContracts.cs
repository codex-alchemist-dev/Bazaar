namespace Bazaar.Server.VmBench.Abstractions;

/// <summary>Which VM of an agent's pair a measurement is about (docs/design/16-agent-vms-and-environments.md, "Testing").</summary>
public enum BenchGuestOs
{
    Windows,
    Linux,
}

/// <summary>
/// The measurements doc 16 lists for the benchmark (not CI): idle and loaded RAM, boot-to-ready, save, restore and checkpoint times,
/// share throughput, Godot headless export, Linux build time and parallel pairs.
/// </summary>
public enum VmBenchScenario
{
    IdleRam,
    LoadedRam,
    BootToReady,
    Save,
    Restore,
    Checkpoint,
    ShareThroughput,
    GodotHeadlessExport,
    LinuxBuild,
    ParallelPairs,
}

/// <summary>A reference to one benchmark VM. Opaque to the harness; the host that created it knows what it means.</summary>
public sealed record BenchGuest(string Id, BenchGuestOs Os);

/// <summary>A guest workload the benchmark times: a Godot headless export or a Linux build of a fixed sample.</summary>
public enum BenchWorkload
{
    GodotHeadlessExport,
    LinuxBuild,
    /// <summary>A memory-and-CPU load used for the loaded-RAM scenario. Never touches the share.</summary>
    SteadyLoad,
}

/// <summary>One measurement. <paramref name="Value"/> is in <paramref name="Unit"/>; a failed attempt has <paramref name="Failed"/> set and a note.</summary>
public sealed record VmBenchSample(
    VmBenchScenario Scenario,
    BenchGuestOs Os,
    int Iteration,
    double Value,
    string Unit,
    DateTimeOffset At,
    bool Failed = false,
    string? Note = null);

/// <summary>
/// The hands the benchmark uses on a real VM pair. The Hyper-V host implements it over <c>IVmBackend</c>, the guest channel and the
/// mount manager; tests use a fake. Nothing here is a feature of the VM module: it is measurement plumbing.
/// </summary>
public interface IVmBenchHost
{
    /// <summary>Creates and starts a pair (one Windows and one Linux guest) and returns its guests.</summary>
    Task<IReadOnlyList<BenchGuest>> StartPairAsync(CancellationToken ct);

    /// <summary>Retires the pair's VMs. The harness always calls this, also after a failure.</summary>
    Task RetireAsync(IReadOnlyList<BenchGuest> guests, CancellationToken ct);

    /// <summary>Starts a stopped guest and returns when the guest agent answers (boot-to-ready).</summary>
    Task<TimeSpan> BootToReadyAsync(BenchGuest guest, CancellationToken ct);

    Task<TimeSpan> SaveAsync(BenchGuest guest, CancellationToken ct);

    Task<TimeSpan> RestoreAsync(BenchGuest guest, CancellationToken ct);

    Task<TimeSpan> CheckpointAsync(BenchGuest guest, CancellationToken ct);

    /// <summary>Resident memory the host assigns to the guest right now, in bytes.</summary>
    Task<long> ReadMemoryBytesAsync(BenchGuest guest, CancellationToken ct);

    /// <summary>Writes then reads <paramref name="bytes"/> through the inter-VM share and returns bytes per second.</summary>
    Task<double> MeasureShareThroughputAsync(BenchGuest guest, long bytes, CancellationToken ct);

    /// <summary>Runs a fixed workload in the guest and returns its wall time.</summary>
    Task<TimeSpan> RunWorkloadAsync(BenchGuest guest, BenchWorkload workload, CancellationToken ct);

    /// <summary>Begins a steady load in the guest. Stopped by <see cref="RetireAsync"/>.</summary>
    Task StartLoadAsync(BenchGuest guest, CancellationToken ct);
}

/// <summary>Run options. Counts are small because the benchmark is run by hand on the server host.</summary>
/// <param name="Iterations">Measured repetitions per scenario.</param>
/// <param name="WarmupIterations">Repetitions run and thrown away before measuring.</param>
/// <param name="ParallelPairCounts">Pair counts for the parallel-pairs scenario, in order (for example 1, 2, 4).</param>
/// <param name="ShareBytes">Bytes moved per share throughput iteration.</param>
/// <param name="Scenarios">Scenarios to run; empty means all.</param>
/// <param name="StepTimeout">Per-measurement limit; a step past it is recorded as failed, never as a result.</param>
public sealed record VmBenchOptions(
    int Iterations = 5,
    int WarmupIterations = 1,
    IReadOnlyList<int>? ParallelPairCounts = null,
    long ShareBytes = 256L * 1024 * 1024,
    IReadOnlyList<VmBenchScenario>? Scenarios = null,
    TimeSpan? StepTimeout = null);

/// <summary>Summary statistics of the successful samples of one scenario and guest OS.</summary>
public sealed record VmBenchStat(
    VmBenchScenario Scenario,
    BenchGuestOs Os,
    string Unit,
    int Count,
    int Failures,
    double Min,
    double Median,
    double P95,
    double Max,
    double Mean);

/// <summary>The finished run: raw samples plus summaries and the context needed to compare runs.</summary>
public sealed record VmBenchReport(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string HostLabel,
    VmBenchOptions Options,
    IReadOnlyList<VmBenchSample> Samples,
    IReadOnlyList<VmBenchStat> Stats);

/// <summary>Runs the benchmark against a host and returns the report. Never throws for a failed measurement; it records it.</summary>
public interface IVmBenchRunner
{
    Task<VmBenchReport> RunAsync(IVmBenchHost host, VmBenchOptions options, string hostLabel, CancellationToken ct);
}

/// <summary>Renders a report. Two formats: Markdown for people and JSON for tools and run-to-run comparison.</summary>
public interface IVmBenchReportWriter
{
    string ToMarkdown(VmBenchReport report);

    string ToJson(VmBenchReport report);
}
