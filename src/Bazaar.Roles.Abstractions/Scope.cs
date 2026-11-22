namespace Bazaar.Roles.Abstractions;

/// <summary>The kinds of scope a grant can name (doc 48 section 4).</summary>
public enum ScopeKind
{
    Instance,
    Company,
    Gc,
    GcTag,
    Module,
    Repository,
    AgentGroup,
}

/// <summary>
/// Where a grant applies. <see cref="Id"/> is empty for <see cref="ScopeKind.Instance"/> and <see cref="ScopeKind.Company"/>
/// (one company per install) and names the GC, tag, module, repository or agent group otherwise.
/// </summary>
public readonly record struct Scope
{
    public Scope(ScopeKind kind, string id = "")
    {
        ArgumentNullException.ThrowIfNull(id);
        var wide = kind is ScopeKind.Instance or ScopeKind.Company;
        if (wide && id.Length != 0)
            throw new ArgumentException($"A {kind} scope takes no id.", nameof(id));
        if (!wide && string.IsNullOrWhiteSpace(id))
            throw new ArgumentException($"A {kind} scope needs an id.", nameof(id));
        Kind = kind;
        Id = id.Trim();
    }

    public ScopeKind Kind { get; }

    public string Id { get; }

    public static Scope Instance { get; } = new(ScopeKind.Instance);

    public static Scope Company { get; } = new(ScopeKind.Company);

    public static Scope Gc(string gcId) => new(ScopeKind.Gc, gcId);

    public static Scope Module(string moduleId) => new(ScopeKind.Module, moduleId);

    /// <summary>
    /// True when a grant at this scope covers <paramref name="requested"/> (doc 48 section 4: a scope grant covers its children).
    /// The instance covers everything and the company covers everything but the instance. Any other scope covers itself, plus
    /// whatever <paramref name="containment"/> reports as inside it (for example the GCs that carry a GC tag).
    /// </summary>
    public bool Covers(Scope requested, IScopeContainment? containment = null)
    {
        if (Kind == ScopeKind.Instance)
            return true;
        if (Kind == ScopeKind.Company)
            return requested.Kind != ScopeKind.Instance;
        if (this == requested)
            return true;
        return containment?.Contains(this, requested) ?? false;
    }
}

/// <summary>
/// Tells whether one scope sits inside another, for nesting the role store cannot know (a GC inside a GC tag, a repository
/// inside a module). Without an implementation only exact matches, the instance and the company cover anything.
/// </summary>
public interface IScopeContainment
{
    /// <summary>True when <paramref name="inner"/> is a child of <paramref name="outer"/>.</summary>
    bool Contains(Scope outer, Scope inner);
}
