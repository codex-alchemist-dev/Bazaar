using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.Tests;

public sealed class DensityStressTests
{
    private const long _totalMb = 32768;
    private const long _reserveMb = 4096;
    private const long _budgetMb = _totalMb - _reserveMb;

    /// <summary>A host with a fixed memory budget that also checks the admission invariant on every change.</summary>
    private sealed class StressHost : IHostMemoryProbe, IDensityVmDirectory, IDensityVmControl
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<Guid, RunningVm> _running = [];
        private readonly Dictionary<Guid, int> _admitted = [];
        private readonly List<string> _violations = [];
        private readonly DateTimeOffset _longAgo = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        private int _saves;

        public IReadOnlyList<string> Violations
        {
            get
            {
                lock (_lock)
                    return [.. _violations];
            }
        }

        public int Saves => Volatile.Read(ref _saves);

        public int RunningCount
        {
            get
            {
                lock (_lock)
                    return _running.Count;
            }
        }

        public Task<HostMemory> ReadAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
                return Task.FromResult(new HostMemory(_totalMb, _totalMb - _running.Values.Sum(r => (long)r.Vm.MemoryMb)));
        }

        public Task<IReadOnlyList<RunningVm>> ListRunningAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
                return Task.FromResult<IReadOnlyList<RunningVm>>([.. _running.Values]);
        }

        public async Task SaveAsync(Guid vmId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _saves);
            await Task.Delay(1, cancellationToken);
            lock (_lock)
                _running.Remove(vmId);
        }

        /// <summary>The governor handed out a ticket: the committed memory (running plus admitted) must fit the budget.</summary>
        public void Admitted(DensityVm vm)
        {
            lock (_lock)
            {
                _admitted[vm.VmId] = vm.MemoryMb;
                CheckInvariant("admit");
            }
        }

        public void Started(DensityVm vm, bool busy)
        {
            lock (_lock)
            {
                _admitted.Remove(vm.VmId);
                _running[vm.VmId] = new RunningVm(vm, new VmActivity(busy, false, false, _longAgo), true);
                CheckInvariant("start");
            }
        }

        public void Abandoned(DensityVm vm)
        {
            lock (_lock)
                _admitted.Remove(vm.VmId);
        }

        public void Stopped(DensityVm vm)
        {
            lock (_lock)
                _running.Remove(vm.VmId);
        }

        private void CheckInvariant(string where)
        {
            long committed = _running.Values.Sum(r => (long)r.Vm.MemoryMb) + _admitted.Values.Sum(m => (long)m);
            if (committed > _budgetMb)
                _violations.Add($"{where}: running + admitted = {committed} MiB > {_budgetMb} MiB");
        }
    }

    [Fact]
    public async Task Two_hundred_random_concurrent_admissions_never_overcommit_and_all_finish()
    {
        const int count = 200;
        var host = new StressHost();
        var options = new VmDensityOptions
        {
            HostReserveMb = _reserveMb,
            MinIdleBeforeSave = TimeSpan.Zero,
            QueueTimeout = TimeSpan.FromSeconds(30),
            RecheckInterval = TimeSpan.FromMilliseconds(3),
            SaveSettleDelay = TimeSpan.FromMilliseconds(1),
            SaveSettleChecks = 3,
            MaxQueueLength = 1000,
        };
        await using var governor = new DensityGovernor(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System, host, host, host);

        var random = new Random(20261119);
        var plans = Enumerable.Range(1, count).Select(i => new
        {
            Vm = new DensityVm(FakeHost.Id(i), $"agent-{random.Next(20)}", random.Next(2) == 0 ? DensityGuestOs.Windows : DensityGuestOs.Linux, 1024 * random.Next(1, 9)),
            Busy = random.Next(10) < 3,
            StartDelayMs = random.Next(0, 15),
            RunMs = random.Next(0, 4),
            FailStart = random.Next(10) == 0,
        }).ToList();

        int admittedCount = 0;
        int failedCount = 0;
        Task[] tasks = [.. plans.Select(async plan =>
        {
            await Task.Delay(plan.StartDelayMs);
            AdmissionTicket ticket;
            try
            {
                ticket = await governor.AdmitAsync(plan.Vm, CancellationToken.None);
            }
            catch (DensityAdmissionException)
            {
                Interlocked.Increment(ref failedCount);
                return;
            }

            host.Admitted(plan.Vm);
            Interlocked.Increment(ref admittedCount);
            if (plan.FailStart)
            {
                host.Abandoned(plan.Vm);
                await ticket.DisposeAsync();
                return;
            }

            host.Started(plan.Vm, plan.Busy);
            await ticket.ConfirmStartedAsync();
            await Task.Delay(plan.RunMs);
            host.Stopped(plan.Vm);
            governor.NotifyMemoryChanged();
        })];

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Empty(host.Violations);
        Assert.Equal(count, admittedCount + failedCount);
        Assert.Equal(0, failedCount); // nothing here can wait past the 30 s timeout: busy VMs stop within milliseconds
        var snapshot = governor.Snapshot();
        Assert.Equal(0, snapshot.ReservedMb);
        Assert.Equal(0, snapshot.Queued);
        Assert.True(host.Saves > 0, "the workload should have forced the governor to save idle VMs");
    }
}
