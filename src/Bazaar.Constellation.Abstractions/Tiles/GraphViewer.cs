namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Who is looking at the graph (doc 19, "Visibility"). It has the same shape as the Vault's caller, so the graph applies the Vault
/// access rule of doc 18: the owner reads everything, an agent reads the shared areas, its own area and private memory
/// (<see cref="AgentOwner"/>, "agent:&lt;id&gt;") and the areas of the projects it is a member of (<see cref="Projects"/>), and
/// anyone else reads nothing.
/// </summary>
public sealed record GraphViewer(bool IsOwner, string? AgentOwner)
{
    private readonly string[] _projects = [];

    public static GraphViewer Owner { get; } = new(IsOwner: true, AgentOwner: null);

    /// <summary>The slugs of the projects the viewer is a member of, kept sorted and distinct. Empty by default, so no project area is readable.</summary>
    public IReadOnlyList<string> Projects
    {
        get => _projects;
        init => _projects = [.. (value ?? throw new ArgumentNullException(nameof(value))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A key for everything this viewer may see: viewers with equal scopes get the same projection, so a cache may share
    /// one entry between them and must never share it with another scope. A slug holds no slash, so the joined list is unambiguous.
    /// </summary>
    public string Scope => IsOwner ? "owner" : AgentOwner is null ? "shared"
        : _projects.Length == 0 ? "private:" + AgentOwner
        : "private:" + AgentOwner + "/" + string.Join('/', _projects);

    public bool Equals(GraphViewer? other) =>
        other is not null && IsOwner == other.IsOwner && AgentOwner == other.AgentOwner && _projects.AsSpan().SequenceEqual(other._projects);

    public override int GetHashCode() => HashCode.Combine(IsOwner, AgentOwner, _projects.Length);
}
