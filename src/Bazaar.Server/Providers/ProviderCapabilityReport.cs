namespace Bazaar.Server.Providers;

/// <summary>
/// The startup view of every role's selection. The host logs <see cref="Lines"/> once and refuses to start
/// when <see cref="MissingRequiredRoles"/> is not empty (doc 03, startup step 5).
/// </summary>
public sealed class ProviderCapabilityReport
{
    public ProviderCapabilityReport(IEnumerable<ProviderSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        Selections = selections.ToList();
    }

    public IReadOnlyList<ProviderSelection> Selections { get; }

    /// <summary>One log line per selection, in the order the selections were given.</summary>
    public IReadOnlyList<string> Lines => Selections.Select(s => s.ToLogLine()).ToList();

    /// <summary>Required roles that have no provider: missing from the report, disabled with <c>none</c>, or unusable under <c>auto</c>.</summary>
    public IReadOnlyList<ProviderRole> MissingRequiredRoles(IEnumerable<ProviderRole> requiredRoles)
    {
        ArgumentNullException.ThrowIfNull(requiredRoles);

        return requiredRoles
            .Distinct()
            .Where(role => !Selections.Any(s => s.Role == role && s.Provider is not null))
            .ToList();
    }
}
