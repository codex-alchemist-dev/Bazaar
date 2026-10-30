using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Community.Quotas;

/// <summary>Outcome of one community quota check.</summary>
/// <param name="Allowed">True when every relevant bucket had room and the action may proceed.</param>
/// <param name="ExhaustedScope">The scope that refused the action (a <see cref="CommunityQuotaKeys"/> scope, or <see cref="CommunityQuotaGuard.FrozenScope"/>); null when allowed.</param>
public sealed record QuotaDecision(bool Allowed, string? ExhaustedScope);

/// <summary>Records which (agent, platform) pairs are frozen after a quota breach (doc 36: breach denies, then freezes).</summary>
public interface ICommunityFreezeStore
{
    /// <summary>Marks the pair as frozen.</summary>
    void Freeze(string agentId, string platform);

    /// <summary>True when the pair is frozen.</summary>
    bool IsFrozen(string agentId, string platform);

    /// <summary>Clears the freeze. An owner action; the caller is responsible for authorization.</summary>
    void Unfreeze(string agentId, string platform);
}

/// <summary>Process-local freeze records. Thread-safe; cleared when the process restarts.</summary>
public sealed class InMemoryCommunityFreezeStore : ICommunityFreezeStore
{
    private readonly object _gate = new();
    private readonly HashSet<(string AgentId, string Platform)> _frozen = new();

    /// <inheritdoc />
    public void Freeze(string agentId, string platform)
    {
        CommunityQuotaKeys.ValidateIdentity(agentId, platform);
        lock (_gate)
        {
            _frozen.Add((agentId, platform));
        }
    }

    /// <inheritdoc />
    public bool IsFrozen(string agentId, string platform)
    {
        CommunityQuotaKeys.ValidateIdentity(agentId, platform);
        lock (_gate)
        {
            return _frozen.Contains((agentId, platform));
        }
    }

    /// <inheritdoc />
    public void Unfreeze(string agentId, string platform)
    {
        CommunityQuotaKeys.ValidateIdentity(agentId, platform);
        lock (_gate)
        {
            _frozen.Remove((agentId, platform));
        }
    }
}

/// <summary>
/// Community quota checks on the shared rate store (doc 15 <c>rate_bucket</c>). A frozen pair is denied before any
/// bucket is touched. Any breach denies the action and freezes the (agent, platform) pair.
/// </summary>
public sealed class CommunityQuotaGuard
{
    /// <summary>Denied outcome when the pair is frozen. Nothing is consumed.</summary>
    public const string FrozenScope = "frozen";

    private readonly IRateBucketStore _buckets;
    private readonly ICommunityFreezeStore _freezes;

    /// <param name="buckets">Shared counter store.</param>
    /// <param name="freezes">Freeze records for breaching pairs.</param>
    public CommunityQuotaGuard(IRateBucketStore buckets, ICommunityFreezeStore freezes)
    {
        _buckets = buckets ?? throw new ArgumentNullException(nameof(buckets));
        _freezes = freezes ?? throw new ArgumentNullException(nameof(freezes));
    }

    /// <summary>
    /// Checks one outward action. Order: frozen pair, then the repository bucket (repo-scoped actions only), then the
    /// platform bucket. Checking the repository first means a refused repository check never consumes the platform
    /// bucket. A platform refusal after the repository bucket was consumed still leaves that repository count used;
    /// the shared store has no refund, so that case is a known limitation.
    /// </summary>
    /// <param name="repoRef">Repository as <c>owner/name</c>. Required for NewIssue and NewPullRequest.</param>
    /// <exception cref="ArgumentException">Invalid agent, platform or repository input.</exception>
    public QuotaDecision Check(string agentId, string platform, CommunityAction action, string? repoRef, DateTimeOffset now)
    {
        CommunityQuotaKeys.ValidateIdentity(agentId, platform);
        var platformRule = CommunityQuotaRules.RuleFor(action);
        var repoRule = CommunityQuotaRules.RuleForRepo(action);
        if (repoRule is not null && string.IsNullOrWhiteSpace(repoRef))
        {
            throw new ArgumentException("This action needs a repository reference.", nameof(repoRef));
        }

        if (_freezes.IsFrozen(agentId, platform))
        {
            return new QuotaDecision(false, FrozenScope);
        }

        if (repoRule is not null)
        {
            var repoKey = CommunityQuotaKeys.RepoKey(agentId, platform, repoRef!, CommunityQuotaKeys.RepoIssuePrScope);
            if (!_buckets.TryConsume(repoKey, repoRule, now).Allowed)
            {
                return Breach(agentId, platform, CommunityQuotaKeys.RepoIssuePrScope);
            }
        }

        if (platformRule is not null)
        {
            var key = CommunityQuotaKeys.Key(agentId, platform, CommunityQuotaKeys.PublicItemsScope);
            if (!_buckets.TryConsume(key, platformRule, now).Allowed)
            {
                return Breach(agentId, platform, CommunityQuotaKeys.PublicItemsScope);
            }
        }

        return new QuotaDecision(true, null);
    }

    /// <summary>Checks the weekly new-repository quota for an agent on a platform.</summary>
    /// <exception cref="ArgumentException">Invalid agent or platform input.</exception>
    public QuotaDecision CheckNewRepo(string agentId, string platform, DateTimeOffset now)
    {
        CommunityQuotaKeys.ValidateIdentity(agentId, platform);
        if (_freezes.IsFrozen(agentId, platform))
        {
            return new QuotaDecision(false, FrozenScope);
        }

        var key = CommunityQuotaKeys.Key(agentId, platform, CommunityQuotaKeys.NewReposScope);
        if (!_buckets.TryConsume(key, CommunityQuotaRules.RuleForNewRepo(), now).Allowed)
        {
            return Breach(agentId, platform, CommunityQuotaKeys.NewReposScope);
        }

        return new QuotaDecision(true, null);
    }

    /// <summary>True when the (agent, platform) pair is frozen.</summary>
    public bool IsFrozen(string agentId, string platform) => _freezes.IsFrozen(agentId, platform);

    /// <summary>Owner action: clears the freeze on the pair. Buckets keep their current counts.</summary>
    public void Unfreeze(string agentId, string platform) => _freezes.Unfreeze(agentId, platform);

    private QuotaDecision Breach(string agentId, string platform, string scope)
    {
        _freezes.Freeze(agentId, platform);
        return new QuotaDecision(false, scope);
    }
}
