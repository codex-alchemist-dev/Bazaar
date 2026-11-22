using System.Collections.Immutable;

namespace Bazaar.Server.VmEgress.Matching;

/// <summary>A compiled allowlist. Immutable, so the proxy can swap in a new one atomically.</summary>
public sealed class Allowlist
{
    /// <summary>An allowlist that allows nothing.</summary>
    public static readonly Allowlist Empty = new([]);

    private Allowlist(ImmutableArray<AllowlistEntry> entries) => Entries = entries;

    /// <summary>The parsed entries, deduplicated, in canonical order.</summary>
    public ImmutableArray<AllowlistEntry> Entries { get; }

    /// <summary>Parses every entry. Returns false, with the first bad entry, when any entry is not valid; nothing is skipped silently.</summary>
    public static bool TryCompile(IEnumerable<string> entries, out Allowlist result, out string? badEntry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var parsed = new SortedDictionary<string, AllowlistEntry>(StringComparer.Ordinal);
        foreach (var text in entries)
        {
            if (!AllowlistEntry.TryParse(text, out var entry))
            {
                result = Empty;
                badEntry = text;
                return false;
            }

            parsed[entry.Canonical] = entry;
        }

        result = new Allowlist([.. parsed.Values]);
        badEntry = null;
        return true;
    }

    /// <summary>Whether a request for <paramref name="host"/> (as written by the client) on <paramref name="port"/> is allowed. Unparseable hosts are not.</summary>
    public bool IsAllowed(string? host, int port) =>
        HostName.TryNormalize(host, out var name) && IsAllowed(name, port);

    /// <summary>Whether a request for the already normalised <paramref name="host"/> is allowed.</summary>
    public bool IsAllowed(HostName host, int port)
    {
        foreach (var entry in Entries)
        {
            if (entry.Matches(host, port))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The canonical entries of <paramref name="proposed"/> that this list does not already cover. Empty means the change is not growth.</summary>
    public IReadOnlyList<string> Additions(Allowlist proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var added = new List<string>();
        foreach (var entry in proposed.Entries)
        {
            if (!Entries.Any(e => e.Covers(entry)))
            {
                added.Add(entry.Canonical);
            }
        }

        return added;
    }
}
