using Bazaar.OfflineSync;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>A Vault push intent as the Vault module receives it. The offline module carries it and never runs git.</summary>
/// <param name="WriteId">The write that carried the intent (idempotency key).</param>
/// <param name="DeviceId">The device that made the commit.</param>
/// <param name="Repository">The Vault repository the write targets.</param>
/// <param name="Branch">The branch the device pushed.</param>
/// <param name="HeadCommit">The commit the device wants to land.</param>
/// <param name="BaseCommit">The main-branch commit the device started from.</param>
public sealed record VaultPushIntent(Guid WriteId, Guid DeviceId, string Repository, string Branch, string HeadCommit, string BaseCommit);

/// <summary>What the Vault module did with a push intent (doc 56 section 5, item 8).</summary>
public abstract record VaultMergeResult
{
    /// <summary>The commit landed on the main branch.</summary>
    /// <param name="NewHead">The Vault head after the merge.</param>
    public sealed record Merged(string NewHead) : VaultMergeResult;

    /// <summary>The merge conflicted; the pushed branch is kept and a review item was opened.</summary>
    /// <param name="BranchName">The conflict branch, for example "conflict/&lt;device&gt;/&lt;timestamp&gt;".</param>
    /// <param name="ReviewItemId">The review item that asks a person to decide.</param>
    public sealed record ConflictBranch(string BranchName, string ReviewItemId) : VaultMergeResult;

    /// <summary>The Vault refused the intent.</summary>
    /// <param name="Reason">Why it was refused.</param>
    public sealed record Rejected(string Reason) : VaultMergeResult;
}

/// <summary>
/// The Vault module's side of the offline Vault commit merge. Implemented by the Vault module through this seam; the
/// offline module never implements git itself (doc 56 section 5, item 8).
/// </summary>
public interface IVaultOfflineMerger
{
    /// <summary>Lands or refuses one push intent.</summary>
    /// <param name="intent">The push intent.</param>
    /// <param name="cancellationToken">Cancels the merge.</param>
    /// <returns>The merge result.</returns>
    ValueTask<VaultMergeResult> MergeAsync(VaultPushIntent intent, CancellationToken cancellationToken);
}

/// <summary>
/// Merge rule for Vault commits (doc 56 section 5, item 8). It validates the payload, then delegates to
/// <see cref="IVaultOfflineMerger"/> and maps the result to an outcome: Merged to Applied, ConflictBranch to Conflict,
/// Rejected to Rejected. A payload that is malformed, names another device, or carries a bad branch or commit id is
/// rejected before the merger runs.
/// </summary>
public sealed class VaultCommitRule : IOfflineMergeRule
{
    private const int _maxBranchLength = 200;
    private const int _sha1Length = 40;
    private const int _sha256Length = 64;

    private readonly IVaultOfflineMerger _merger;

    /// <summary>Creates the rule over the Vault module's merger.</summary>
    /// <param name="merger">The Vault merger.</param>
    public VaultCommitRule(IVaultOfflineMerger merger)
    {
        ArgumentNullException.ThrowIfNull(merger);
        _merger = merger;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.VaultCommit;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        var payload = VaultCommitPayloadCodec.Read(write.Payload);
        if (payload is null)
        {
            return RuleOutcomes.Rejected(write.WriteId, "Vault commit payload is malformed.");
        }

        if (payload.Device != write.DeviceId)
        {
            return RuleOutcomes.Rejected(write.WriteId, "Vault commit names a different device than the write.");
        }

        if (!IsValidBranch(payload.Branch))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Vault commit branch name is invalid.");
        }

        if (!IsCommitId(payload.HeadCommit) || !IsCommitId(payload.BaseCommit))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Vault commit ids must be full lowercase SHA hex digests.");
        }

        var intent = new VaultPushIntent(write.WriteId, write.DeviceId, write.Target, payload.Branch, payload.HeadCommit, payload.BaseCommit);
        var result = await _merger.MergeAsync(intent, cancellationToken);
        return result switch
        {
            VaultMergeResult.Merged merged => RuleOutcomes.Applied(write.WriteId, merged.NewHead),
            VaultMergeResult.ConflictBranch conflict => new OfflineWriteOutcome(
                write.WriteId,
                OfflineOutcomeKind.Conflict,
                Reason: $"Conflict on branch {conflict.BranchName}; review item {conflict.ReviewItemId} opened."),
            VaultMergeResult.Rejected rejected => RuleOutcomes.Rejected(write.WriteId, rejected.Reason),
            _ => throw new InvalidOperationException($"Unknown Vault merge result {result.GetType().Name}."),
        };
    }

    private static bool IsValidBranch(string branch)
    {
        if (branch.Length is 0 or > _maxBranchLength || branch[0] == '-' || branch.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in branch)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCommitId(string commit)
    {
        if (commit.Length is not (_sha1Length or _sha256Length))
        {
            return false;
        }

        foreach (var c in commit)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Registration of the Vault commit rule.</summary>
public static class VaultCommitRuleRegistration
{
    /// <summary>
    /// Registers <see cref="VaultCommitRule"/> only when an <see cref="IVaultOfflineMerger"/> is already registered. Without
    /// one the rule is absent, and the batch processor reports <see cref="OfflineOutcomeKind.ModuleUnavailable"/> for Vault
    /// commits. Call this after the Vault module has registered its merger.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarVaultCommitRule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static d => d.ServiceType == typeof(IVaultOfflineMerger)))
        {
            return services;
        }

        services.AddSingleton<IOfflineMergeRule>(static sp => new VaultCommitRule(sp.GetRequiredService<IVaultOfflineMerger>()));
        return services;
    }
}
