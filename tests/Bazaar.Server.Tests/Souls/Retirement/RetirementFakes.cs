using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Retirement;

namespace Bazaar.Server.Tests.Souls.Retirement;

/// <summary>Port calls in the order they happened, shared by every fake in one test.</summary>
internal sealed class CallLog
{
    public List<string> Entries { get; } = [];
}

/// <summary>
/// One fake port operation. Like a real port it is idempotent: the first success reports <see cref="RetirementStepStatus.Done"/>
/// and later calls report <see cref="RetirementStepStatus.AlreadyDone"/>. Set <see cref="Fail"/> to make it throw, or
/// <see cref="Override"/> to return a fixed status.
/// </summary>
internal sealed class FakeOp(string name, CallLog log)
{
    private readonly HashSet<object> _done = [];

    /// <summary>How many times the port was called, including failed calls.</summary>
    public int Calls { get; private set; }

    /// <summary>When set, the next calls throw this exception.</summary>
    public Exception? Fail { get; set; }

    /// <summary>When set, calls return this status instead of the idempotent result.</summary>
    public RetirementStepStatus? Override { get; set; }

    public Task<RetirementStepStatus> Invoke(object key)
    {
        Calls++;
        log.Entries.Add(name);
        if (Fail is not null)
            return Task.FromException<RetirementStepStatus>(Fail);
        if (Override is { } forced)
            return Task.FromResult(forced);
        if (_done.Contains(key))
            return Task.FromResult(RetirementStepStatus.AlreadyDone);
        _done.Add(key);
        return Task.FromResult(RetirementStepStatus.Done);
    }
}

internal sealed class FakePassportRevoker(CallLog log) : IPassportRevoker
{
    public FakeOp Op { get; } = new("passport", log);

    public Task<RetirementStepStatus> RevokeAsync(SoulRetirementSubject subject, CancellationToken cancellationToken) => Op.Invoke(subject);
}

internal sealed class FakeTokenRevoker(CallLog log) : ITokenRevoker
{
    public FakeOp Op { get; } = new("token", log);

    public Task<RetirementStepStatus> RevokeAsync(SoulRetirementSubject subject, CancellationToken cancellationToken) => Op.Invoke(subject);
}

internal sealed class FakeVmDiskRetirer(CallLog log) : IVmDiskRetirer
{
    public FakeOp Stop { get; } = new("vm-stop", log);

    public FakeOp ChildDelete { get; } = new("child-delete", log);

    public FakeOp ParentDelete { get; } = new("parent-delete", log);

    public Task<RetirementStepStatus> StopAsync(VmChildDisk disk, CancellationToken cancellationToken) => Stop.Invoke(disk);

    public Task<RetirementStepStatus> DeleteChildDiskAsync(VmChildDisk disk, CancellationToken cancellationToken) => ChildDelete.Invoke(disk);

    public Task<RetirementStepStatus> DeleteParentDiskAsync(VmParentDisk disk, CancellationToken cancellationToken) => ParentDelete.Invoke(disk);
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Everything one retirement test needs, wired to fakes and one shared call log.</summary>
internal sealed class RetirementRig
{
    public RetirementRig(DateTimeOffset now)
    {
        Log = new CallLog();
        Passports = new FakePassportRevoker(Log);
        Tokens = new FakeTokenRevoker(Log);
        Disks = new FakeVmDiskRetirer(Log);
        Clock = new ManualClock(now);
    }

    public CallLog Log { get; }

    public FakePassportRevoker Passports { get; }

    public FakeTokenRevoker Tokens { get; }

    public FakeVmDiskRetirer Disks { get; }

    public InMemoryRetirementLedger Ledger { get; } = new();

    public ManualClock Clock { get; }

    public RetirementCoordinator Coordinator => new(Passports, Tokens, Disks, Ledger, Clock);
}
