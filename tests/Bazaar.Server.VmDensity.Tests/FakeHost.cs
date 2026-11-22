using Bazaar.Server.VmDensity.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDensity.Tests;

/// <summary>A fake host: memory probe, VM directory and VM control in one, with hooks to hold and fail saves.</summary>
internal sealed class FakeHost : IHostMemoryProbe, IDensityVmDirectory, IDensityVmControl
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, RunningVm> _running = [];
    private readonly Dictionary<Guid, RunningVm> _ghosts = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _gates = [];
    private readonly HashSet<Guid> _failing = [];
    private readonly List<Guid> _saveCalls = [];
    private long _unreturnedMb;

    public FakeHost(TimeProvider time, long totalMb)
    {
        _time = time;
        TotalMb = totalMb;
    }

    public long TotalMb { get; }

    /// <summary>When true, a saved VM's memory stays unavailable until <see cref="ReturnMemory"/>.</summary>
    public bool DelayMemoryReturn { get; set; }

    /// <summary>When true, a saved VM keeps being listed as running (a stale directory).</summary>
    public bool KeepListedAfterSave { get; set; }

    /// <summary>When set, the probe throws.</summary>
    public Exception? ProbeFailure { get; set; }

    public IReadOnlyList<Guid> SaveCalls
    {
        get
        {
            lock (_lock)
                return [.. _saveCalls];
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_lock)
                return _running.Count;
        }
    }

    public static Guid Id(int n) => new(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    public RunningVm Add(int n, DensityGuestOs os, string agent, int mb, double idleMinutes = 30, bool busy = false, bool canSave = true)
    {
        var vm = new RunningVm(
            new DensityVm(Id(n), agent, os, mb),
            new VmActivity(busy, false, false, _time.GetUtcNow() - TimeSpan.FromMinutes(idleMinutes)),
            canSave);
        lock (_lock)
            _running[vm.Vm.VmId] = vm;
        return vm;
    }

    public void Remove(int n)
    {
        lock (_lock)
            _running.Remove(Id(n));
    }

    public void Hold(int n)
    {
        lock (_lock)
            _gates[Id(n)] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release(int n)
    {
        TaskCompletionSource? gate;
        lock (_lock)
            _gates.TryGetValue(Id(n), out gate);
        gate?.TrySetResult();
    }

    public void FailSave(int n)
    {
        lock (_lock)
            _failing.Add(Id(n));
    }

    public void ReturnMemory()
    {
        lock (_lock)
            _unreturnedMb = 0;
    }

    public Task<HostMemory> ReadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (ProbeFailure is not null)
                throw ProbeFailure;
            long used = _running.Values.Sum(r => (long)r.Vm.MemoryMb) + _unreturnedMb;
            return Task.FromResult(new HostMemory(TotalMb, TotalMb - used));
        }
    }

    public Task<IReadOnlyList<RunningVm>> ListRunningAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<RunningVm>>([.. _running.Values, .. _ghosts.Values]);
    }

    public async Task SaveAsync(Guid vmId, CancellationToken cancellationToken)
    {
        TaskCompletionSource? gate;
        lock (_lock)
        {
            _saveCalls.Add(vmId);
            _gates.TryGetValue(vmId, out gate);
        }

        if (gate is not null)
            await gate.Task.WaitAsync(cancellationToken);

        lock (_lock)
        {
            if (_failing.Contains(vmId))
                throw new InvalidOperationException("save failed");
            if (_running.Remove(vmId, out RunningVm? vm))
            {
                if (DelayMemoryReturn)
                    _unreturnedMb += vm.Vm.MemoryMb;
                if (KeepListedAfterSave)
                    _ghosts[vmId] = vm;
            }
        }
    }
}

/// <summary>A governor wired to a <see cref="FakeHost"/> and a fake clock.</summary>
internal sealed class Rig
{
    public Rig(Action<VmDensityOptions>? configure = null, long totalMb = 16384)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        Host = new FakeHost(Time, totalMb);
        Options = new VmDensityOptions
        {
            HostReserveMb = 4096,
            MinIdleBeforeSave = TimeSpan.FromMinutes(2),
            QueueTimeout = TimeSpan.FromMinutes(10),
            RecheckInterval = TimeSpan.FromSeconds(5),
            SaveSettleChecks = 6,
            SaveSettleDelay = TimeSpan.FromMilliseconds(500),
        };
        configure?.Invoke(Options);
        Governor = new DensityGovernor(Microsoft.Extensions.Options.Options.Create(Options), Time, Host, Host, Host);
    }

    public FakeTimeProvider Time { get; }

    public FakeHost Host { get; }

    public VmDensityOptions Options { get; }

    public DensityGovernor Governor { get; }

    public static DensityVm Vm(int n, int mb, DensityGuestOs os = DensityGuestOs.Linux, string agent = "req") =>
        new(FakeHost.Id(n), agent, os, mb);

    public Task<AdmissionTicket> Admit(int n, int mb, string agent = "req", CancellationToken? ct = null) =>
        Governor.AdmitAsync(Vm(n, mb, agent: agent), ct ?? TestContext.Current.CancellationToken);

    /// <summary>Waits (real time) until the condition holds.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what = "condition")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(2);
        }
    }

    /// <summary>Drives fake time forward in small steps until the task completes, so settle delays and rechecks fire.</summary>
    public async Task<T> Pump<T>(Task<T> task, TimeSpan? step = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Task did not complete while pumping time.");
            Time.Advance(step ?? TimeSpan.FromMilliseconds(600));
            await Task.Delay(2);
        }

        return await task;
    }

    /// <summary>Drives fake time forward until the condition holds.</summary>
    public async Task PumpUntil(Func<bool> condition, TimeSpan? step = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Condition not met while pumping time.");
            Time.Advance(step ?? TimeSpan.FromMilliseconds(600));
            await Task.Delay(2);
        }
    }

    public bool HasEvent(string kind) => Governor.Snapshot().RecentEvents.Any(e => e.Kind == kind);

    public int EventCount(string kind) => Governor.Snapshot().RecentEvents.Count(e => e.Kind == kind);
}
