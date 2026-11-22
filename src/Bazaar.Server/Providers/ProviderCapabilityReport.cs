namespace Bazaar.Server.Providers;

/// <summary>
/// The startup view of every role's selection. The host logs <see cref="Lines"/> once and refuses to start
/// when <see cref="MissingRequiredRoles"/> is not empty (doc 03, startup step 5).
/// </summary>
/// <remarks>
/// A role may start as <see cref="ProviderSelection.Pending"/> and be replaced once the host has made its selection. Reads and the
/// replacement are safe from several threads: the status page can read while the host resolves a role.
/// </remarks>
public sealed class ProviderCapabilityReport
{
    private readonly object _gate = new();
    private readonly List<ProviderSelection> _selections;

    public ProviderCapabilityReport(IEnumerable<ProviderSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        _selections = selections.ToList();
    }

    /// <summary>A snapshot of the selections, in the order they were given. Later <see cref="Resolve"/> calls are not reflected.</summary>
    public IReadOnlyList<ProviderSelection> Selections
    {
        get
        {
            lock (_gate)
            {
                return _selections.ToList();
            }
        }
    }

    /// <summary>One log line per selection, in the order the selections were given.</summary>
    public IReadOnlyList<string> Lines => Selections.Select(s => s.ToLogLine()).ToList();

    /// <summary>
    /// Required roles that have no provider: missing from the report, disabled with <c>none</c>, or unusable under <c>auto</c>. A role that
    /// is still pending is not reported here, because the host has not decided it yet.
    /// </summary>
    public IReadOnlyList<ProviderRole> MissingRequiredRoles(IEnumerable<ProviderRole> requiredRoles)
    {
        ArgumentNullException.ThrowIfNull(requiredRoles);

        var snapshot = Selections;
        return requiredRoles
            .Distinct()
            .Where(role => !snapshot.Any(s => s.Role == role && (s.Provider is not null || s.IsPending)))
            .ToList();
    }

    /// <summary>
    /// Replaces the selection of the same role, or adds it when the role has none yet. A pending selection is replaced the same way.
    /// </summary>
    public void Resolve(ProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        lock (_gate)
        {
            var index = _selections.FindIndex(s => s.Role == selection.Role);
            if (index < 0)
            {
                _selections.Add(selection);
            }
            else
            {
                _selections[index] = selection;
            }
        }
    }
}
