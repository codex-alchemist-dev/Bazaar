namespace Bazaar.Server.WhatIf;

/// <summary>Deterministic stand-in for the sandbox VM host. Records every spec it was given and every session it handed out.</summary>
public sealed class FakeSandboxVmHost : ISandboxVmHost
{
    private readonly List<SandboxVmSpec> _specs = [];
    private readonly List<FakeSandboxSession> _sessions = [];

    public Exception? ProvisionFailure { get; set; }

    public Func<string, SandboxTaskResult>? TaskBehaviour { get; set; }

    public BranchMeasurement Measurement { get; set; } = new(true, 40, ReviewBand.High, 5_000);

    /// <summary>When set, new sessions wait on it inside a task, so a test can cancel mid-run.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<SandboxVmSpec> Specs
    {
        get
        {
            lock (_specs)
                return [.. _specs];
        }
    }

    public IReadOnlyList<FakeSandboxSession> Sessions
    {
        get
        {
            lock (_sessions)
                return [.. _sessions];
        }
    }

    public Task<ISandboxSession> ProvisionAsync(SandboxVmSpec spec, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_specs)
            _specs.Add(spec);
        if (ProvisionFailure is not null)
            return Task.FromException<ISandboxSession>(ProvisionFailure);
        var session = new FakeSandboxSession($"vm-{spec.RunId:N}-{spec.Branch}", this);
        lock (_sessions)
            _sessions.Add(session);
        return Task.FromResult<ISandboxSession>(session);
    }
}

/// <summary>A fake sandbox session. Teardown is idempotent and counted.</summary>
public sealed class FakeSandboxSession(string sessionId, FakeSandboxVmHost owner) : ISandboxSession
{
    private readonly List<string> _tasks = [];
    private int _teardownCalls;
    private int _tornDown;

    public string SessionId { get; } = sessionId;

    public Exception? CollectFailure { get; set; }

    public Exception? TeardownFailure { get; set; }

    public IReadOnlyList<string> Tasks
    {
        get
        {
            lock (_tasks)
                return [.. _tasks];
        }
    }

    public int TeardownCalls => Volatile.Read(ref _teardownCalls);

    /// <summary>How many times the VM was actually destroyed (at most once).</summary>
    public int TornDown => Volatile.Read(ref _tornDown);

    public async Task<SandboxTaskResult> RunTaskAsync(string task, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_tasks)
            _tasks.Add(task);
        if (owner.Gate is { } gate)
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return owner.TaskBehaviour?.Invoke(task) ?? new SandboxTaskResult(true);
    }

    public Task<BranchMeasurement> CollectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return CollectFailure is null ? Task.FromResult(owner.Measurement) : Task.FromException<BranchMeasurement>(CollectFailure);
    }

    public Task TeardownAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _teardownCalls);
        if (TeardownFailure is not null)
            return Task.FromException(TeardownFailure);
        Interlocked.CompareExchange(ref _tornDown, 1, 0);
        return Task.CompletedTask;
    }
}

/// <summary>Fake host-side push gate. It refuses any ref outside <c>sandbox/*</c> the way the real gate must, and records pushes.</summary>
public sealed class FakeSandboxPushGate : ISandboxPushGate
{
    private readonly List<SandboxPushRequest> _pushes = [];

    public Exception? FailWith { get; set; }

    /// <summary>When set, the gate reports this ref instead of the requested one, to simulate a misbehaving host.</summary>
    public string? ReportedRef { get; set; }

    public IReadOnlyList<SandboxPushRequest> Pushes
    {
        get
        {
            lock (_pushes)
                return [.. _pushes];
        }
    }

    public Task<string> PushAsync(SandboxPushRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (!SandboxRequestGuard.IsSandboxRef(request.Ref))
            return Task.FromException<string>(new SandboxRequestRejectedException([$"pushes may only target {SandboxRequestGuard.RefPrefix}* refs"]));
        if (FailWith is not null)
            return Task.FromException<string>(FailWith);
        lock (_pushes)
            _pushes.Add(request);
        return Task.FromResult(ReportedRef ?? request.Ref);
    }
}

/// <summary>Fake quota: grants until <see cref="Remaining"/> runs out, and refuses windows outside <see cref="AllowedWindows"/>.</summary>
public sealed class FakeSandboxQuota : ISandboxQuota
{
    private readonly List<SandboxQuotaRequest> _requests = [];
    private int _remaining = int.MaxValue;

    public int Remaining
    {
        get => Volatile.Read(ref _remaining);
        set => Volatile.Write(ref _remaining, value);
    }

    public IReadOnlySet<WorkLifeWindow> AllowedWindows { get; set; } = new HashSet<WorkLifeWindow> { WorkLifeWindow.Break, WorkLifeWindow.Off };

    public IReadOnlyList<SandboxQuotaRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    public Task<SandboxQuotaDecision> TryConsumeAsync(SandboxQuotaRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_requests)
        {
            _requests.Add(request);
            if (!AllowedWindows.Contains(request.Window))
                return Task.FromResult(SandboxQuotaDecision.Refuse(SandboxQuotaRefusal.OutsideWindow));
            if (_remaining <= 0)
                return Task.FromResult(SandboxQuotaDecision.Refuse(SandboxQuotaRefusal.QuotaExhausted));
            _remaining--;
            return Task.FromResult(SandboxQuotaDecision.Allow);
        }
    }
}
