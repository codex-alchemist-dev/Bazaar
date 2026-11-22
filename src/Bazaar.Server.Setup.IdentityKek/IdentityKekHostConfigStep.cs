using System.Collections.Concurrent;

namespace Bazaar.Server.Setup.IdentityKek;

/// <summary>
/// Applies <c>custody.identityKek</c> (doc 45 step 5, PROPOSED) by writing the host configuration file <see cref="IdentityKekHostConfig"/>.
/// An absent field writes nothing and leaves any existing file alone. A second apply with the same value changes nothing. Rollback
/// restores the previous file, or removes the one this run created. The value is a provider kind, never a secret.
/// </summary>
public sealed class IdentityKekHostConfigStep(Func<string> dataRoot) : ISetupStep
{
    /// <summary>Order after the install settings step (1500) and before verification.</summary>
    public const int StepOrder = 1510;

    private readonly ConcurrentDictionary<string, byte[]?> _previousByPlanHash = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => "identity.kek.hostconfig";

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers { get; } = ["custody.identityKek"];

    /// <inheritdoc />
    public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        var kind = plan.Custody.IdentityKek;
        if (kind is null)
        {
            return Task.FromResult(new SetupStepResult(true, "No identity KEK provider in the plan; the server default stays."));
        }

        try
        {
            var previous = IdentityKekHostConfig.Write(dataRoot(), kind);
            _previousByPlanHash.TryAdd(context.PlanHash, previous);
            return Task.FromResult(new SetupStepResult(true, "Identity KEK provider written to the host configuration file."));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new SetupStepResult(false, "custody.identityKek is not an allowed provider kind."));
        }
        catch (IOException)
        {
            return Task.FromResult(new SetupStepResult(false, "The host configuration file could not be written."));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new SetupStepResult(false, "The host configuration file could not be written (access denied)."));
        }
    }

    /// <inheritdoc />
    public Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        if (plan.Custody.IdentityKek is not null && _previousByPlanHash.TryRemove(context.PlanHash, out var previous))
        {
            IdentityKekHostConfig.Restore(dataRoot(), previous);
        }

        return Task.CompletedTask;
    }
}
