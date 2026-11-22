namespace Bazaar.Server.Wire.Discovery;

/// <summary>Merges every registered <see cref="IWireDiscoveryContribution"/>; the lists are rebuilt on each read.</summary>
public sealed class WireDiscoveryCatalog(IEnumerable<IWireDiscoveryContribution> contributions) : IWireDiscoveryCatalog
{
    private readonly IWireDiscoveryContribution[] _contributions = [.. contributions];

    /// <inheritdoc />
    public IReadOnlyList<string> Transports => Merge(static c => c.Transports);

    /// <inheritdoc />
    public IReadOnlyList<string> Codecs => Merge(static c => c.Codecs);

    private string[] Merge(Func<IWireDiscoveryContribution, IReadOnlyList<string>> select)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var contribution in _contributions)
        {
            foreach (var name in select(contribution))
            {
                // Only plain ids: a module cannot smuggle a URL, host name or free text into the public document.
                if (IsPlainId(name) && seen.Add(name))
                {
                    result.Add(name);
                }
            }
        }

        return [.. result];
    }

    internal static bool IsPlainId(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 32)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
