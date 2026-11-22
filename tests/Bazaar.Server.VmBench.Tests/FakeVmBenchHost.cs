using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench.Tests;

/// <summary>
/// Deterministic <see cref="IVmBenchHost"/>. Each call to a method returns a value derived from its per-method call index
/// (1-based, across all guests), so tests can predict every sample. Fault hooks let a test fail, hang or cancel a named step.
/// </summary>
public sealed class FakeVmBenchHost : IVmBenchHost
{
    private readonly object _lock = new();

    /// <summary>Call count per method name, for example "SaveAsync".</summary>
    public Dictionary<string, int> Calls { get; } = [];

    /// <summary>Every pair started, in order. The first is the main pair.</summary>
    public List<IReadOnlyList<BenchGuest>> StartedPairs { get; } = [];

    /// <summary>Every retire call: the guests and whether the token it received was already cancelled.</summary>
    public List<(IReadOnlyList<BenchGuest> Guests, bool TokenCancelled)> Retired { get; } = [];

    /// <summary>Method name whose calls throw an <see cref="InvalidOperationException"/>.</summary>
    public string? ThrowOn { get; set; }

    /// <summary>Method name whose calls never return until the token is cancelled (to exercise the step timeout).</summary>
    public string? HangOn { get; set; }

    /// <summary>Called at the start of every measured method, with its name. Tests use it to cancel the run.</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>Decides, from the method name and its 1-based call index, whether that call throws.</summary>
    public Func<string, int, bool>? FailWhen { get; set; }

    /// <summary>Share throughput the fake reports, in bytes per second: 128 MiB/s.</summary>
    public const double ShareBytesPerSecond = 128d * 1024 * 1024;

    public Task<IReadOnlyList<BenchGuest>> StartPairAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var n = StartedPairs.Count + 1;
            IReadOnlyList<BenchGuest> pair = [new BenchGuest($"win-{n}", BenchGuestOs.Windows), new BenchGuest($"lin-{n}", BenchGuestOs.Linux)];
            StartedPairs.Add(pair);
            return Task.FromResult(pair);
        }
    }

    public Task RetireAsync(IReadOnlyList<BenchGuest> guests, CancellationToken ct)
    {
        lock (_lock)
        {
            Calls["RetireAsync"] = Calls.GetValueOrDefault("RetireAsync") + 1;
            Retired.Add((guests, ct.IsCancellationRequested));
        }

        return Task.CompletedTask;
    }

    public async Task<TimeSpan> BootToReadyAsync(BenchGuest guest, CancellationToken ct)
    {
        var index = await EnterAsync(nameof(BootToReadyAsync), ct);
        return TimeSpan.FromSeconds(index);
    }

    public async Task<TimeSpan> SaveAsync(BenchGuest guest, CancellationToken ct)
    {
        var index = await EnterAsync(nameof(SaveAsync), ct);
        return TimeSpan.FromSeconds(index);
    }

    public async Task<TimeSpan> RestoreAsync(BenchGuest guest, CancellationToken ct)
    {
        var index = await EnterAsync(nameof(RestoreAsync), ct);
        return TimeSpan.FromSeconds(index);
    }

    public async Task<TimeSpan> CheckpointAsync(BenchGuest guest, CancellationToken ct)
    {
        var index = await EnterAsync(nameof(CheckpointAsync), ct);
        return TimeSpan.FromSeconds(index);
    }

    /// <summary>Returns the call index in MiB, so a memory sample equals its call index.</summary>
    public async Task<long> ReadMemoryBytesAsync(BenchGuest guest, CancellationToken ct)
    {
        var index = await EnterAsync(nameof(ReadMemoryBytesAsync), ct);
        return index * 1024L * 1024L;
    }

    public async Task<double> MeasureShareThroughputAsync(BenchGuest guest, long bytes, CancellationToken ct)
    {
        await EnterAsync(nameof(MeasureShareThroughputAsync), ct);
        return ShareBytesPerSecond;
    }

    public async Task<TimeSpan> RunWorkloadAsync(BenchGuest guest, BenchWorkload workload, CancellationToken ct)
    {
        await EnterAsync(nameof(RunWorkloadAsync), ct);
        return TimeSpan.FromSeconds(30);
    }

    public async Task StartLoadAsync(BenchGuest guest, CancellationToken ct)
    {
        await EnterAsync(nameof(StartLoadAsync), ct);
    }

    /// <summary>Counts the call, runs the hooks, and returns the 1-based call index for this method.</summary>
    private async Task<int> EnterAsync(string name, CancellationToken ct)
    {
        int index;
        lock (_lock)
        {
            index = Calls.GetValueOrDefault(name) + 1;
            Calls[name] = index;
        }

        OnCall?.Invoke(name);
        ct.ThrowIfCancellationRequested();

        if (HangOn == name)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        if (ThrowOn == name || (FailWhen?.Invoke(name, index) ?? false))
        {
            throw new InvalidOperationException($"fake {name} failed");
        }

        return index;
    }
}
