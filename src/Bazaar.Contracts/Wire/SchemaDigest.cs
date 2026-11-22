using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Contracts.Wire;

/// <summary>
/// The schema identity of doc 04: a stable digest of the sorted <c>namespace:highest-migration</c> pairs. A build computes it from
/// its embedded migration files and a server from the migrations it applied, so a client updater can check equality. The digest says
/// nothing about order or which side is newer.
/// </summary>
public static class SchemaDigest
{
    /// <summary>Prefix of every identity string.</summary>
    public const string Prefix = "sha256:";

    /// <summary>
    /// Computes the identity from <c>(namespace, migration number)</c> pairs, one per applied or embedded migration. Only the highest
    /// number of each namespace counts, so the input order and the count of files per namespace do not matter.
    /// </summary>
    /// <exception cref="ArgumentException">A namespace is empty or holds a character that would break the canonical text, or a number is negative.</exception>
    public static string Compute(IEnumerable<(string Namespace, int Version)> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var highest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (ns, version) in migrations)
        {
            if (string.IsNullOrWhiteSpace(ns) || ns.AsSpan().ContainsAny(':', '\n', '\r'))
                throw new ArgumentException("A migration namespace must be non-empty and hold no ':' or line break.", nameof(migrations));
            if (version < 0)
                throw new ArgumentException("A migration number cannot be negative.", nameof(migrations));
            highest[ns] = highest.TryGetValue(ns, out var seen) ? Math.Max(seen, version) : version;
        }

        var canonical = string.Join(
            '\n',
            highest.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}"));
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
