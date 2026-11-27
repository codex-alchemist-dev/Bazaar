using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Core;

/// <summary>
/// Who performed an action: a <see cref="PrincipalKind"/> plus an identifier. The canonical text form is
/// <c>kind:id</c> with a lowercase kind, for example <c>user:dom</c>, <c>service:kek</c> or <c>system:setup</c>.
/// </summary>
/// <remarks>
/// The id is 1 to 128 characters with no control characters, whitespace, ':' or '/', so the text form splits at
/// the first ':' without ambiguity. Ids compare ordinally and case-sensitively. Equality and ordering are by kind
/// (in <see cref="PrincipalKind"/> order), then id. The text form is the serialised form: use
/// <see cref="ToString"/> and <see cref="TryParse(string?, out Principal?)"/>. Acting for someone else is
/// <see cref="PrincipalAction"/>, not a field here.
/// </remarks>
public sealed class Principal : IEquatable<Principal>, IComparable<Principal>
{
    private Principal(PrincipalKind kind, string id)
    {
        Kind = kind;
        Id = id;
    }

    /// <summary>The kind of actor.</summary>
    public PrincipalKind Kind { get; }

    /// <summary>The identifier, unique within its kind.</summary>
    public string Id { get; }

    /// <summary>Creates a principal, throwing <see cref="ArgumentException"/> when the id is invalid.</summary>
    public static Principal Create(PrincipalKind kind, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!TextForm.IsValidSegment(id))
        {
            throw new ArgumentException(
                "A principal id is 1 to 128 characters with no control characters, whitespace, ':' or '/'.", nameof(id));
        }

        return new Principal(kind, id);
    }

    /// <summary>Shorthand for <see cref="Create"/> with <see cref="PrincipalKind.Agent"/>.</summary>
    public static Principal Agent(string id) => Create(PrincipalKind.Agent, id);

    /// <summary>Shorthand for <see cref="Create"/> with <see cref="PrincipalKind.User"/>.</summary>
    public static Principal User(string id) => Create(PrincipalKind.User, id);

    /// <summary>Shorthand for <see cref="Create"/> with <see cref="PrincipalKind.Service"/>.</summary>
    public static Principal Service(string id) => Create(PrincipalKind.Service, id);

    /// <summary>Shorthand for <see cref="Create"/> with <see cref="PrincipalKind.System"/>.</summary>
    public static Principal System(string id) => Create(PrincipalKind.System, id);

    /// <summary>Parses the canonical <c>kind:id</c> form, throwing <see cref="FormatException"/> when it is invalid.</summary>
    public static Principal Parse(string text) =>
        TryParse(text, out var principal) ? principal : throw new FormatException($"'{text}' is not a valid principal.");

    /// <summary>Parses the canonical <c>kind:id</c> form. The kind must be lowercase; no trimming is done.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Principal? principal)
    {
        principal = null;
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        if (!TryParseKind(text.AsSpan(0, separator), out var kind))
        {
            return false;
        }

        var id = text[(separator + 1)..];
        if (!TextForm.IsValidSegment(id))
        {
            return false;
        }

        principal = new Principal(kind, id);
        return true;
    }

    private static bool TryParseKind(ReadOnlySpan<char> text, out PrincipalKind kind)
    {
        switch (text)
        {
            case "agent":
                kind = PrincipalKind.Agent;
                return true;
            case "user":
                kind = PrincipalKind.User;
                return true;
            case "service":
                kind = PrincipalKind.Service;
                return true;
            case "system":
                kind = PrincipalKind.System;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string KindText(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Agent => "agent",
        PrincipalKind.User => "user",
        PrincipalKind.Service => "service",
        _ => "system",
    };

    /// <summary>The canonical <c>kind:id</c> text form.</summary>
    public override string ToString() => KindText(Kind) + ":" + Id;

    /// <inheritdoc />
    public bool Equals(Principal? other) =>
        other is not null && Kind == other.Kind && string.Equals(Id, other.Id, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Principal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Id);

    /// <inheritdoc />
    public int CompareTo(Principal? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byKind = Kind.CompareTo(other.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(Id, other.Id);
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Principal? left, Principal? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Principal? left, Principal? right) => !(left == right);

    /// <summary>Less-than by <see cref="CompareTo"/>.</summary>
    public static bool operator <(Principal? left, Principal? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Less-than-or-equal by <see cref="CompareTo"/>.</summary>
    public static bool operator <=(Principal? left, Principal? right) => left is null || left.CompareTo(right) <= 0;

    /// <summary>Greater-than by <see cref="CompareTo"/>.</summary>
    public static bool operator >(Principal? left, Principal? right) => left is not null && left.CompareTo(right) > 0;

    /// <summary>Greater-than-or-equal by <see cref="CompareTo"/>.</summary>
    public static bool operator >=(Principal? left, Principal? right) => left is null ? right is null : left.CompareTo(right) >= 0;
}
