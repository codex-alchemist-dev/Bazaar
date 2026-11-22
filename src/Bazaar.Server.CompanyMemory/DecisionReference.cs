using Bazaar.Server.Knowledge.CompanyMemory;

namespace Bazaar.Server.CompanyMemory;

/// <summary>Turns the ways an agent may name a decision (<c>0002</c>, <c>0002-slug</c>, a path) into one Vault path.</summary>
internal static class DecisionReference
{
    private const string _prefix = "decisions/";
    private const string _suffix = ".md";

    /// <summary>
    /// Returns the matching record's path, or null when nothing matches. A bare number matches the one record that starts
    /// with it. Throws <see cref="ArgumentException"/> for a blank reference.
    /// </summary>
    public static string? Resolve(string reference, IReadOnlyCollection<DecisionRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var name = Normalise(reference);
        var exact = _prefix + name + _suffix;
        if (records.Any(r => r.Path == exact))
        {
            return exact;
        }

        if (name.Length > 0 && name.All(char.IsAsciiDigit))
        {
            var padded = name.PadLeft(4, '0') + "-";
            var matches = records.Where(r => r.Path.StartsWith(_prefix + padded, StringComparison.Ordinal)).ToList();
            return matches.Count == 1 ? matches[0].Path : null;
        }

        return null;
    }

    /// <summary>The path a reference names when written the way the Vault stores it. Does not check that it exists.</summary>
    public static string ToPath(string reference) => _prefix + Normalise(reference) + _suffix;

    private static string Normalise(string reference)
    {
        var name = reference.Trim();
        if (name.StartsWith(_prefix, StringComparison.Ordinal))
        {
            name = name[_prefix.Length..];
        }

        if (name.EndsWith(_suffix, StringComparison.Ordinal))
        {
            name = name[..^_suffix.Length];
        }

        return name;
    }
}
