using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.CodingGit;

/// <summary>
/// A branch name that is always <c>agent/&lt;task&gt;</c> (doc 17, Git workflow). The task slug uses
/// <c>[a-z0-9._-]</c>, is 1 to 100 characters, never contains <c>..</c>, never starts with <c>.</c> or <c>-</c>,
/// and never ends with <c>.</c> or <c>.lock</c>. The default value is not a valid name.
/// </summary>
public readonly struct BranchName : IEquatable<BranchName>
{
    /// <summary>The required prefix.</summary>
    public const string Prefix = "agent/";

    /// <summary>Longest allowed task slug.</summary>
    public const int MaxTaskLength = 100;

    private readonly string? _value;

    private BranchName(string value)
    {
        _value = value;
    }

    /// <summary>True for <c>default(BranchName)</c>, which carries no name.</summary>
    public bool IsDefault => _value is null;

    /// <summary>The full branch name, for example <c>agent/fix-login</c>.</summary>
    /// <exception cref="InvalidOperationException">This is the default value.</exception>
    public string Value => _value ?? throw new InvalidOperationException("The default BranchName carries no name.");

    /// <summary>The task slug after the prefix.</summary>
    public string TaskSlug => Value[Prefix.Length..];

    /// <summary>Parses a full branch name.</summary>
    /// <exception cref="CodingGitException">The name is not a valid agent branch.</exception>
    public static BranchName Parse(string? value) =>
        TryParse(value, out var result)
            ? result
            : throw new CodingGitException(CodingGitError.InvalidBranch, "The branch name must be agent/<task> with a task slug of [a-z0-9._-].");

    /// <summary>Builds the branch name for a task slug.</summary>
    /// <exception cref="CodingGitException">The slug is not valid.</exception>
    public static BranchName ForTask(string? taskSlug) => Parse(Prefix + taskSlug);

    /// <summary>Parses a full branch name without throwing.</summary>
    public static bool TryParse(string? value, out BranchName result)
    {
        result = default;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsValidSlug(value.AsSpan(Prefix.Length)))
        {
            return false;
        }

        result = new BranchName(value);
        return true;
    }

    private static bool IsValidSlug(ReadOnlySpan<char> slug)
    {
        if (slug.Length is 0 or > MaxTaskLength)
        {
            return false;
        }

        foreach (var c in slug)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return slug[0] is not ('.' or '-')
            && slug[^1] is not '.'
            && !slug.Contains("..", StringComparison.Ordinal)
            && !slug.EndsWith(".lock", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(BranchName other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is BranchName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public override string ToString() => _value ?? string.Empty;

    /// <summary>Value equality.</summary>
    public static bool operator ==(BranchName left, BranchName right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(BranchName left, BranchName right) => !left.Equals(right);
}
