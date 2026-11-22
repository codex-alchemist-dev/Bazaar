namespace Bazaar.Server.CodingGit.Fakes;

/// <summary>Fake <see cref="IHostPushPort"/>. Records what reached it and opens draft pull requests.</summary>
public sealed class FakeHostPushPort : IHostPushPort
{
    private readonly List<HostPushRequest> _requests = [];
    private int _number;

    /// <summary>Requests that reached the port, in order.</summary>
    public IReadOnlyList<HostPushRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>When true the port misbehaves and reports a non-draft pull request. For fail-closed tests.</summary>
    public bool ReturnNonDraft { get; set; }

    /// <summary>When set, the port throws this instead of opening a pull request.</summary>
    public Exception? FailWith { get; set; }

    /// <inheritdoc />
    public Task<PullRequestRef> PushDraftPrAsync(HostPushRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_requests)
        {
            _requests.Add(request);
        }

        if (FailWith is { } failure)
        {
            throw failure;
        }

        var number = Interlocked.Increment(ref _number);
        return Task.FromResult(new PullRequestRef(request.Repo.Id, number, $"https://example.invalid/{request.Repo.Id}/pull/{number}", !ReturnNonDraft));
    }
}

/// <summary>Fake <see cref="IPushGate"/> that returns a fixed decision and records every request.</summary>
public sealed class FakePushGate : IPushGate
{
    private readonly List<PushGateRequest> _requests = [];

    /// <summary>The decision returned. Allow by default.</summary>
    public PushDecision Decision { get; set; } = PushDecision.Allow();

    /// <summary>Requests the gate saw.</summary>
    public IReadOnlyList<PushGateRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<PushDecision> EvaluateAsync(PushGateRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return ValueTask.FromResult(Decision);
    }
}

/// <summary>Fake <see cref="IAgentGitIdentity"/> that derives an identity from the agent id.</summary>
public sealed class FakeAgentGitIdentity : IAgentGitIdentity
{
    /// <inheritdoc />
    public ValueTask<GitAuthor> GetAuthorAsync(string agentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GitAuthor($"{agentId}[bot]", $"{agentId}@users.noreply.example.invalid"));
}

/// <summary>Fake <see cref="IWorkShareLayout"/> with fixed folders.</summary>
/// <param name="WorktreeRoot">The work share root returned for every agent.</param>
/// <param name="PassportFolder">The Passport folder returned for every agent.</param>
public sealed class FixedWorkShareLayout(string WorktreeRoot, string PassportFolder) : IWorkShareLayout
{
    /// <inheritdoc />
    public string GetWorktreeRoot(string agentId) => WorktreeRoot;

    /// <inheritdoc />
    public string GetPassportFolder(string agentId) => PassportFolder;
}
