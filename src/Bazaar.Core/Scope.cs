using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Core;

/// <summary>
/// Where something applies: a <see cref="ScopeKind"/> plus an id, and for tenants an optional sub-path of further
/// segments. The canonical text form is <c>kind:id</c> or <c>tenant:id/seg/seg</c>, for example
/// <c>install:main</c>, <c>tenant:acme</c>, <c>tenant:acme/branch/7</c> or <c>user:dom</c>.
/// </summary>
/// <remarks>
/// The id and every path segment follow the <see cref="Principal"/> id rules (1 to 128 characters, no control
/// characters, whitespace, ':' or '/'), so the first ':' ends the kind and '/' splits the path. Only tenants have
/// a sub-path. Equality and ordering are by kind (in <see cref="ScopeKind"/> order), id, then path segments,
/// all ordinal. Scopes of different kinds never contain each other: whether a tenant belongs to an install is
/// data the caller owns, not something the text form can say.
/// </remarks>
public sealed class Scope : IEquatable<Scope>, IComparable<Scope>
{
    private readonly string[] _path;

    private Scope(ScopeKind kind, string id, string[] path)
    {
        Kind = kind;
        Id = id;
        _path = path;
    }

    /// <summary>The kind of scope.</summary>
    public ScopeKind Kind { get; }

    /// <summary>The identifier of the install, tenant or user.</summary>
    public string Id { get; }

    /// <summary>The sub-path inside a tenant, empty for the tenant as a whole and for other kinds.</summary>
    public IReadOnlyList<string> Path => _path;

    /// <summary>Creates a scope, throwing <see cref="ArgumentException"/> when an id or path segment is invalid.</summary>
    /// <param name="kind">The scope kind.</param>
    /// <param name="id">The install, tenant or user id.</param>
    /// <param name="path">Sub-path segments; allowed only for <see cref="ScopeKind.Tenant"/>.</param>
    public static Scope Create(ScopeKind kind, string id, params string[] path)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!TextForm.IsValidSegment(id))
        {
            throw new ArgumentException(
                "A scope id is 1 to 128 characters with no control characters, whitespace, ':' or '/'.", nameof(id));
        }

        if (path.Length > 0 && kind != ScopeKind.Tenant)
        {
            throw new ArgumentException("Only a tenant scope has a sub-path.", nameof(path));
        }

        foreach (var segment in path)
        {
            if (segment is null || !TextForm.IsValidSegment(segment))
            {
                throw new ArgumentException(
                    "A scope path segment is 1 to 128 characters with no control characters, whitespace, ':' or '/'.", nameof(path));
            }
        }

        return new Scope(kind, id, (string[])path.Clone());
    }

    /// <summary>The scope of one installation.</summary>
    public static Scope Install(string id) => Create(ScopeKind.Install, id);

    /// <summary>The scope of a tenant, or of a sub-unit inside it when <paramref name="path"/> is given.</summary>
    public static Scope Tenant(string id, params string[] path) => Create(ScopeKind.Tenant, id, path);

    /// <summary>The scope of one user's own data.</summary>
    public static Scope User(string id) => Create(ScopeKind.User, id);

    /// <summary>Parses the canonical text form, throwing <see cref="FormatException"/> when it is invalid.</summary>
    public static Scope Parse(string text) =>
        TryParse(text, out var scope) ? scope : throw new FormatException($"'{text}' is not a valid scope.");

    /// <summary>Parses the canonical text form. The kind must be lowercase; no trimming is done.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Scope? scope)
    {
        scope = null;
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        ScopeKind kind;
        switch (text.AsSpan(0, separator))
        {
            case "install":
                kind = ScopeKind.Install;
                break;
            case "tenant":
                kind = ScopeKind.Tenant;
                break;
            case "user":
                kind = ScopeKind.User;
                break;
            default:
                return false;
        }

        var segments = text[(separator + 1)..].Split('/');
        if (segments.Length > 1 && kind != ScopeKind.Tenant)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (!TextForm.IsValidSegment(segment))
            {
                return false;
            }
        }

        scope = new Scope(kind, segments[0], segments[1..]);
        return true;
    }

    /// <summary>
    /// True when this scope is the same as, or wider than, <paramref name="other"/>: same kind and id, and this
    /// scope's path is a leading run of the other's segments. <c>tenant:acme</c> contains <c>tenant:acme/branch/7</c>.
    /// </summary>
    public bool Contains(Scope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Kind != other.Kind || !string.Equals(Id, other.Id, StringComparison.Ordinal) || _path.Length > other._path.Length)
        {
            return false;
        }

        for (var i = 0; i < _path.Length; i++)
        {
            if (!string.Equals(_path[i], other._path[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when this scope is the same as, or narrower than, <paramref name="other"/>. The inverse of <see cref="Contains"/>.</summary>
    public bool IsWithin(Scope other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Contains(this);
    }

    /// <summary>The canonical text form.</summary>
    public override string ToString()
    {
        var head = Kind switch
        {
            ScopeKind.Install => "install:",
            ScopeKind.Tenant => "tenant:",
            _ => "user:",
        } + Id;
        return _path.Length == 0 ? head : head + "/" + string.Join('/', _path);
    }

    /// <inheritdoc />
    public bool Equals(Scope? other) =>
        other is not null && Kind == other.Kind && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && _path.AsSpan().SequenceEqual(other._path);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Scope);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Id, StringComparer.Ordinal);
        foreach (var segment in _path)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(Scope? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Kind.CompareTo(other.Kind);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(Id, other.Id);
        if (result != 0)
        {
            return result;
        }

        var shared = Math.Min(_path.Length, other._path.Length);
        for (var i = 0; i < shared; i++)
        {
            result = string.CompareOrdinal(_path[i], other._path[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return _path.Length.CompareTo(other._path.Length);
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Scope? left, Scope? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Scope? left, Scope? right) => !(left == right);

    /// <summary>Less-than by <see cref="CompareTo"/>.</summary>
    public static bool operator <(Scope? left, Scope? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Less-than-or-equal by <see cref="CompareTo"/>.</summary>
    public static bool operator <=(Scope? left, Scope? right) => left is null || left.CompareTo(right) <= 0;

    /// <summary>Greater-than by <see cref="CompareTo"/>.</summary>
    public static bool operator >(Scope? left, Scope? right) => left is not null && left.CompareTo(right) > 0;

    /// <summary>Greater-than-or-equal by <see cref="CompareTo"/>.</summary>
    public static bool operator >=(Scope? left, Scope? right) => left is null ? right is null : left.CompareTo(right) >= 0;
}
