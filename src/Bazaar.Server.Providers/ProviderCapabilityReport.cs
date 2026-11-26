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
/// <summary>One registered provider and what its own <see cref="IProvider.Probe"/> reported. <see cref="Role"/> is null for a provider that implements no role interface.</summary>
public sealed record ProviderReportEntry(ProviderRole? Role, string Kind, ProviderStatus Status)
{
    /// <summary>One line for the startup log and the status page, for example <c>provider search: postgres_fts usable</c>.</summary>
    public string ToLogLine()
    {
        var name = Role is { } role ? $"provider {role.SettingName()}" : "provider";
        return Status.Usable
            ? $"{name}: {Kind} usable"
            : $"{name}: {Kind} unusable ({Status.Reason ?? "unusable"})";
    }
}

/// <summary>
/// The startup view of every registered provider (doc 03, "Provider modules"). Core does not choose among providers: the report lists each
/// one's own status, any number per role, and the consuming module decides what to do with several. It probes on every read, so a provider
/// that becomes usable later (a managed cache child that has started) shows as usable.
/// </summary>
public sealed class ProviderCapabilityReport
{
    private readonly IReadOnlyList<IProvider> _providers;

    public ProviderCapabilityReport(IEnumerable<IProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToList();
    }

    /// <summary>One entry per registered provider and role, in registration order. A provider whose probe throws is reported unusable.</summary>
    public IReadOnlyList<ProviderReportEntry> Entries => _providers.SelectMany(Describe).ToList();

    /// <summary>One log line per entry, in registration order. A role with no provider has no line.</summary>
    public IReadOnlyList<string> Lines => Entries.Select(e => e.ToLogLine()).ToList();

    /// <summary>
    /// Required roles that have no usable provider: no provider is registered for the role, or every one of them is unusable. This is a
    /// readiness requirement (<c>host.readiness.required_roles</c>), not a selection: one usable provider is enough to satisfy it.
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
        var entries = Entries;
        return requiredRoles
            .Distinct()
            .Where(role => !entries.Any(e => e.Role == role && e.Status.Usable))
            .ToList();
    }

    private static IEnumerable<ProviderReportEntry> Describe(IProvider provider)
    {
        var status = Probe(provider);
        var roles = RolesOf(provider);
        return roles.Count == 0
            ? [new ProviderReportEntry(null, provider.Kind, status)]
            : roles.Select(role => new ProviderReportEntry(role, provider.Kind, status));
    }

    private static ProviderStatus Probe(IProvider provider)
    {
        try
        {
            return provider.Probe();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type name only: exception messages can echo settings.
            return new ProviderStatus(provider.Kind, false, [], $"probe failed ({ex.GetType().Name})");
        }
    }

    private static List<ProviderRole> RolesOf(IProvider provider)
    {
        var roles = new List<ProviderRole>();
        if (provider is IServiceHostProvider)
        {
            roles.Add(ProviderRole.ServiceHost);
        }

        if (provider is IVmProvider)
        {
            roles.Add(ProviderRole.Vm);
        }

        if (provider is ICacheProvider)
        {
            roles.Add(ProviderRole.Cache);
        }

        if (provider is ISearchProvider)
        {
            roles.Add(ProviderRole.Search);
        }

        return roles;
    }
}
