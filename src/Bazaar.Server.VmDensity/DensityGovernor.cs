using Bazaar.Server.VmDensity.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.VmDensity;

/// <summary>
/// The density governor (doc 59 section 8, item 1): admits VM starts against free host memory, saving idle VMs to make room and
/// queueing what cannot fit. Density control is an optimisation, not a security control: when <see cref="VmDensityOptions.Enabled"/>
/// is false, when the host registered no <see cref="IHostMemoryProbe"/>, <see cref="IDensityVmDirectory"/> or
/// <see cref="IDensityVmControl"/> (see <see cref="IsActive"/>), or when the probe itself fails, every request is admitted untracked.
/// </summary>
/// <remarks>
/// <para>
/// Locking. A request goes through a FIFO list of waiters. Planning is serialised by one async gate that is held across the host
/// reads and the plan, so reading memory, planning and reserving are atomic: two concurrent admissions cannot both spend the same
/// free memory. Shared state (reservations, in-flight saves, the waiter list, events) sits behind a short synchronous lock that is
/// never held across an await. Saves run after the gate is released, so a slow save does not block other admissions: the requester
/// already holds its reservation and the victims are marked in flight, which stops another admission choosing them or saving more
/// than it needs to. Every exit path of <see cref="AdmitAsync"/> that does not hand out a ticket releases the reservation and
/// removes the waiter.
/// </para>
/// <para>
/// Saves are started in plan order and run concurrently. A save started for a requester that then cancels still runs to completion
/// (its bookkeeping is released by the save itself); only disposing the governor cancels them.
/// </para>
/// </remarks>
public sealed class DensityGovernor : IDensityGovernor, IAsyncDisposable, IDisposable
{
    private const int _maxEvents = 100;

    private readonly IOptions<VmDensityOptions> _options;
    private readonly TimeProvider _time;
    private readonly IHostMemoryProbe? _probe;
    private readonly IDensityVmDirectory? _directory;
    private readonly IDensityVmControl? _control;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();

    private readonly List<Waiter> _waiters = [];
    private readonly Dictionary<Guid, long> _reservations = [];
    private readonly Dictionary<Guid, long> _inFlight = [];
    private readonly Dictionary<Guid, Cooldown> _cooldowns = [];
    private readonly Queue<DensityEvent> _events = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _seq;
    private long _lastFreeMb;
    private int _lastRunning;

    /// <summary>Creates the governor. The three ports are optional: without all of them the governor admits everything.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="time">The clock and timer source.</param>
    /// <param name="probe">Reads host memory.</param>
    /// <param name="directory">Lists running VMs.</param>
    /// <param name="control">Saves VMs.</param>
    public DensityGovernor(
        IOptions<VmDensityOptions> options,
        TimeProvider time,
        IHostMemoryProbe? probe = null,
        IDensityVmDirectory? directory = null,
        IDensityVmControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
        _probe = probe;
        _directory = directory;
        _control = control;
    }

    /// <summary>True when the host supplied every port, so the governor enforces limits. False means it admits everything.</summary>
    public bool IsActive => _probe is not null && _directory is not null && _control is not null;

    /// <inheritdoc />
    public async Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vm);
        VmDensityOptions options = _options.Value;
        if (!options.Enabled || !IsActive)
            return new UntrackedTicket();

        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter(vm);
        lock (_sync)
        {
            if (_waiters.Count > 0 && _waiters.Count >= options.MaxQueueLength)
                throw new DensityAdmissionException(DensityAdmissionFailure.QueueFull, $"The VM start queue is full ({options.MaxQueueLength}).");
            waiter.Seq = ++_seq;
            _waiters.Add(waiter);
        }

        AdmissionTicket? ticket = null;
        using var timeout = new CancellationTokenSource(options.QueueTimeout > TimeSpan.Zero ? options.QueueTimeout : Timeout.InfiniteTimeSpan, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _lifetime.Token);
        try
        {
            ticket = await RunAsync(waiter, options, linked.Token);
            return ticket;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw TimedOut(waiter);
        }
        finally
        {
            if (ticket is null)
                Abandon(waiter);
        }
    }

    /// <inheritdoc />
    public DensitySnapshot Snapshot()
    {
        lock (_sync)
            return new DensitySnapshot(_lastFreeMb, _reservations.Values.Sum(), _lastRunning, _waiters.Count, [.. _events]);
    }

    /// <inheritdoc />
    public void NotifyMemoryChanged() => Signal();

    /// <summary>Cancels waiting requests and any saves still in flight. The governor is not usable afterwards.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Same as <see cref="DisposeAsync"/>, for containers that dispose synchronously.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        Signal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<AdmissionTicket> RunAsync(Waiter waiter, VmDensityOptions options, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            bool eligible;
            lock (_sync)
            {
                changed = _changed.Task;
                eligible = IsEligible(waiter, options);
            }

            if (eligible)
            {
                Step step = await PlanStepAsync(waiter, allowSave: true, token);
                if (step.Ticket is not null)
                    return step.Ticket;
                if (step.Victims is not null)
                {
                    AdmissionTicket? afterSave = await SaveAndSettleAsync(waiter, step.Victims, token);
                    if (afterSave is not null)
                        return afterSave;
                    continue; // replan at once: memory may have returned, or a victim failed and must be skipped
                }
            }

            MarkQueued(waiter);
            if (options.QueueTimeout <= TimeSpan.Zero)
                throw TimedOut(waiter);
            await WaitForChangeAsync(changed, options.RecheckInterval, token);
        }
    }

    private bool IsEligible(Waiter waiter, VmDensityOptions options)
    {
        if (options.AllowSmallerToPass)
            return true;
        foreach (Waiter other in _waiters)
        {
            if (ReferenceEquals(other, waiter))
                return true;
            if (other.State == WaiterState.Queued)
                return false;
        }

        return true;
    }

    /// <summary>
    /// One planning pass under the gate: read memory and the VM list, plan, and either reserve and admit, reserve and mark victims in
    /// flight, or leave the waiter queued. When the host reads fail the request is admitted untracked.
    /// </summary>
    private async Task<Step> PlanStepAsync(Waiter waiter, bool allowSave, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            HostMemory host;
            IReadOnlyList<RunningVm> running;
            try
            {
                host = await _probe!.ReadAsync(token);
                running = await _directory!.ListRunningAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    Record(DensityEventKinds.ProbeFailed, waiter.Vm.VmId, ex.Message);
                    return new Step(Finish(waiter, new UntrackedTicket()), null);
                }
            }

            VmDensityOptions options = _options.Value;
            lock (_sync)
            {
                DateTimeOffset now = _time.GetUtcNow();
                PruneCooldowns(now);

                long ownMb = _reservations.GetValueOrDefault(waiter.Id);
                bool hasOwn = _reservations.ContainsKey(waiter.Id);
                long reservedMb = _reservations.Values.Sum() - ownMb;
                int reservedCount = _reservations.Count - (hasOwn ? 1 : 0);

                var visible = new List<RunningVm>(running.Count);
                long pendingFree = 0;
                int pendingCount = 0;
                foreach (RunningVm r in running)
                {
                    Guid id = r.Vm.VmId;
                    if (id == waiter.Vm.VmId)
                        continue;
                    if (_inFlight.ContainsKey(id))
                    {
                        pendingFree += r.Vm.MemoryMb;
                        pendingCount++;
                        continue;
                    }

                    if (_cooldowns.TryGetValue(id, out Cooldown? cooldown))
                    {
                        if (cooldown.Saved)
                            continue;
                        visible.Add(r with { CanSaveState = false });
                        continue;
                    }

                    visible.Add(r);
                }

                _lastFreeMb = host.FreeMb;
                _lastRunning = visible.Count + pendingCount;

                AdmissionPlan plan = DensityPlanner.Plan(waiter.Vm, host, reservedMb, reservedCount, visible, options, now, pendingFree, pendingCount);
                switch (plan)
                {
                    case AdmissionPlan.Admit:
                        _reservations[waiter.Id] = waiter.Vm.MemoryMb;
                        return new Step(Finish(waiter, new GovernorTicket(this, waiter.Id)), null);

                    case AdmissionPlan.SaveThenAdmit save when allowSave:
                        _reservations[waiter.Id] = waiter.Vm.MemoryMb;
                        waiter.State = WaiterState.Saving;
                        foreach (RunningVm victim in save.Victims)
                            _inFlight[victim.Vm.VmId] = victim.Vm.MemoryMb;
                        return new Step(null, save.Victims);

                    case AdmissionPlan.Reject reject:
                        throw new DensityAdmissionException(
                            reject.Reason,
                            $"VM {waiter.Vm.VmId} needs {waiter.Vm.MemoryMb} MiB; the host offers {host.TotalMb - options.HostReserveMb} MiB after its reserve.");

                    default:
                        return new Step(null, null);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AdmissionTicket?> SaveAndSettleAsync(Waiter waiter, IReadOnlyList<RunningVm> victims, CancellationToken token)
    {
        // Saves start in plan order (each call runs synchronously up to its first await) and then run concurrently.
        var saves = new List<Task<bool>>(victims.Count);
        foreach (RunningVm victim in victims)
            saves.Add(SaveVictimAsync(victim));
        bool[] results = await Task.WhenAll(saves).WaitAsync(token);
        bool anyFailed = results.Contains(false);

        VmDensityOptions options = _options.Value;
        for (int i = 0; ; i++)
        {
            Step step = await PlanStepAsync(waiter, allowSave: false, token);
            if (step.Ticket is not null)
                return step.Ticket;
            if (anyFailed || i >= options.SaveSettleChecks)
                break;
            await Task.Delay(options.SaveSettleDelay, _time, token); // the saved memory has not come back yet
        }

        lock (_sync)
        {
            _reservations.Remove(waiter.Id);
            waiter.State = WaiterState.Queued;
        }

        Signal();
        return null;
    }

    private async Task<bool> SaveVictimAsync(RunningVm victim)
    {
        Guid id = victim.Vm.VmId;
        try
        {
            await _control!.SaveAsync(id, _lifetime.Token);
            lock (_sync)
            {
                _inFlight.Remove(id);
                _cooldowns[id] = new Cooldown(Saved: true, _time.GetUtcNow() + _options.Value.VictimCooldown);
                Record(DensityEventKinds.SavedForRoom, id, $"{victim.Vm.Os} VM of {victim.Vm.AgentId}, {victim.Vm.MemoryMb} MiB");
            }

            Signal();
            return true;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _inFlight.Remove(id);
                _cooldowns[id] = new Cooldown(Saved: false, _time.GetUtcNow() + _options.Value.VictimCooldown);
                Record(DensityEventKinds.SaveFailed, id, ex.Message);
            }

            Signal();
            return false;
        }
    }

    private async Task WaitForChangeAsync(Task changed, TimeSpan interval, CancellationToken token)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task delay = Task.Delay(interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(5), _time, waitCts.Token);
        await Task.WhenAny(changed, delay);
        await waitCts.CancelAsync();
    }

    /// <summary>Removes the waiter and records the admission. Caller holds the lock and has already stored the reservation.</summary>
    private AdmissionTicket Finish(Waiter waiter, AdmissionTicket ticket)
    {
        _waiters.Remove(waiter);
        _cooldowns.Remove(waiter.Vm.VmId);
        if (waiter.QueuedRecorded)
            Record(DensityEventKinds.Dequeued, waiter.Vm.VmId, "admitted after waiting");
        Record(DensityEventKinds.Admitted, waiter.Vm.VmId, $"{waiter.Vm.Os} VM of {waiter.Vm.AgentId}, {waiter.Vm.MemoryMb} MiB");
        return ticket;
    }

    private void MarkQueued(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.QueuedRecorded)
                return;
            waiter.QueuedRecorded = true;
            Record(DensityEventKinds.Queued, waiter.Vm.VmId, $"{waiter.Vm.MemoryMb} MiB waiting for room");
        }
    }

    private DensityAdmissionException TimedOut(Waiter waiter)
    {
        lock (_sync)
            Record(DensityEventKinds.TimedOut, waiter.Vm.VmId, "queue timeout");
        return new DensityAdmissionException(DensityAdmissionFailure.QueueTimeout, $"VM {waiter.Vm.VmId} did not get room within the queue timeout.");
    }

    /// <summary>Drops a waiter that leaves without a ticket (cancelled, timed out, rejected or failed): releases its reservation. Idempotent.</summary>
    private void Abandon(Waiter waiter)
    {
        lock (_sync)
        {
            _waiters.Remove(waiter);
            _reservations.Remove(waiter.Id);
        }

        Signal();
    }

    private void Release(Guid reservationId, bool signal)
    {
        bool released;
        lock (_sync)
            released = _reservations.Remove(reservationId);
        if (released && signal)
            Signal();
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_sync)
        {
            old = _changed;
            _changed = NewSignal();
        }

        old.TrySetResult();
    }

    private void PruneCooldowns(DateTimeOffset now)
    {
        List<Guid>? expired = null;
        foreach ((Guid id, Cooldown cooldown) in _cooldowns)
        {
            if (cooldown.Until <= now)
                (expired ??= []).Add(id);
        }

        if (expired is null)
            return;
        foreach (Guid id in expired)
            _cooldowns.Remove(id);
    }

    private void Record(string kind, Guid vmId, string detail)
    {
        _events.Enqueue(new DensityEvent(_time.GetUtcNow(), kind, vmId, detail));
        while (_events.Count > _maxEvents)
            _events.Dequeue();
    }

    private enum WaiterState
    {
        Queued,
        Saving,
    }

    private sealed class Waiter(DensityVm vm)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public DensityVm Vm { get; } = vm;

        public long Seq { get; set; }

        public WaiterState State { get; set; }

        public bool QueuedRecorded { get; set; }
    }

    private sealed record Cooldown(bool Saved, DateTimeOffset Until);

    private sealed record Step(AdmissionTicket? Ticket, IReadOnlyList<RunningVm>? Victims);

    private sealed class GovernorTicket(DensityGovernor owner, Guid reservationId) : AdmissionTicket
    {
        private int _released;

        public override Task ConfirmStartedAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(reservationId, signal: false);
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(reservationId, signal: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UntrackedTicket : AdmissionTicket
    {
        public override Task ConfirmStartedAsync() => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
