using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>
/// Maps the Vault module's <see cref="IVaultOfflineMergeEngine"/> to <see cref="IVaultOfflineMerger"/> and opens the review
/// item when the engine keeps a conflict branch (doc 56 section 8, item 3). The offline module still runs no git.
/// </summary>
public sealed class VaultOfflineMergerAdapter : IVaultOfflineMerger
{
    /// <summary>The review item kind for a kept Vault conflict branch.</summary>
    public const string ReviewKind = "vault-conflict";

    private readonly IVaultOfflineMergeEngine _engine;
    private readonly IReviewSink _review;
    private readonly TimeProvider _time;

    /// <summary>Creates the adapter.</summary>
    /// <param name="engine">The Vault merge engine for the repository.</param>
    /// <param name="review">Receives the review item for a conflict.</param>
    /// <param name="time">The clock that names conflict branches.</param>
    public VaultOfflineMergerAdapter(IVaultOfflineMergeEngine engine, IReviewSink review, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _review = review;
        _time = time;
    }

    /// <inheritdoc />
    public async ValueTask<VaultMergeResult> MergeAsync(VaultPushIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var request = new VaultOfflineMergeRequest(
            intent.WriteId, intent.DeviceId, intent.Branch, intent.HeadCommit, intent.BaseCommit, _time.GetUtcNow());
        var result = await _engine.MergeAsync(request, cancellationToken);
        switch (result)
        {
            case VaultOfflineMergeResult.Merged merged:
                return new VaultMergeResult.Merged(merged.NewHead);

            case VaultOfflineMergeResult.Conflicted conflicted:
                var reason = $"Offline Vault commit {intent.HeadCommit} overlaps main in {string.Join(", ", conflicted.ConflictedPaths)}; "
                    + $"both versions are kept, the device's on branch {conflicted.BranchName}.";
                await _review.OpenAsync(
                    new ReviewItem(ReviewKind, intent.Repository, intent.WriteId, intent.BaseCommit, conflicted.BranchName, reason),
                    cancellationToken);
                return new VaultMergeResult.ConflictBranch(conflicted.BranchName, intent.WriteId.ToString("D"));

            case VaultOfflineMergeResult.Rejected rejected:
                return new VaultMergeResult.Rejected(rejected.Reason);

            default:
                throw new InvalidOperationException($"Unknown Vault engine result {result.GetType().Name}.");
        }
    }
}

/// <summary>Registration of the Vault merger adapter.</summary>
public static class VaultOfflineMergerAdapterRegistration
{
    /// <summary>
    /// Registers <see cref="VaultOfflineMergerAdapter"/> as the <see cref="IVaultOfflineMerger"/> when the host already
    /// registered an <see cref="IVaultOfflineMergeEngine"/>. Call before <c>AddBazaarVaultCommitRule</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarVaultOfflineMerger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static d => d.ServiceType == typeof(IVaultOfflineMergeEngine))
            || services.Any(static d => d.ServiceType == typeof(IVaultOfflineMerger)))
        {
            return services;
        }

        services.AddSingleton<IVaultOfflineMerger>(static sp => new VaultOfflineMergerAdapter(
            sp.GetRequiredService<IVaultOfflineMergeEngine>(),
            sp.GetRequiredService<IReviewSink>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
