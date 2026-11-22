namespace Bazaar.Server.Providers;

/// <summary>
/// Picks one role's provider from the candidates registered for this OS (doc 03, "Provider selection").
/// <c>auto</c> takes the first usable candidate in list order, so list the preferred one first.
/// An explicit kind must be registered and usable, or the selection throws. <c>none</c> disables the role.
/// </summary>
public static class ProviderSelector
{
    /// <summary>Setting value that asks for the first usable candidate.</summary>
    public const string Auto = "auto";

    /// <summary>Setting value that disables the role.</summary>
    public const string None = "none";

    /// <param name="requiredConcurrency">
    /// Instances the role needs at once. A candidate that is an <see cref="ICapacityLimitedProvider"/> with a lower limit is
    /// unusable for this selection, with the reason in its status (fail closed). The default of 1 needs no capacity.
    /// </param>
    public static ProviderSelection Select<TProvider>(ProviderRole role, string requested, IReadOnlyList<TProvider> candidates, int requiredConcurrency = 1)
        where TProvider : IProvider
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredConcurrency, 1);

        if (requested.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderSelection(role, None, null, []);
        }

        var statuses = candidates.Select(c => CheckCapacity(c.Probe(), c, requiredConcurrency)).ToList();

        if (requested.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (statuses[i].Usable)
                {
                    return new ProviderSelection(role, Auto, candidates[i], statuses);
                }
            }

            return new ProviderSelection(role, Auto, null, statuses);
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates[i].Kind.Equals(requested, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (statuses[i].Usable)
            {
                return new ProviderSelection(role, requested, candidates[i], statuses);
            }

            throw new ProviderSelectionException(role, requested, statuses[i].Reason ?? "not usable on this machine");
        }

        throw new ProviderSelectionException(role, requested, "no provider of that kind is registered for this OS");
    }

    private static ProviderStatus CheckCapacity(ProviderStatus status, IProvider provider, int requiredConcurrency)
    {
        if (!status.Usable || provider is not ICapacityLimitedProvider limited || limited.MaxConcurrentInstances >= requiredConcurrency)
        {
            return status;
        }

        var reason = $"declares {limited.MaxConcurrentInstances} concurrent instance(s) and this role needs {requiredConcurrency}";
        return status with { Usable = false, Reason = reason };
    }
}
