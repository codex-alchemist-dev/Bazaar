namespace Bazaar.Kodo.Manifest;

/// <summary>
/// The probe ids a module may list (doc 46 section 11). The caller builds it from the catalogue file of the signed package, after that
/// file's own signature was checked, so an id here is one the signed catalogue holds. An entry ending in a dot is a family prefix.
/// </summary>
public sealed class ModuleProbeCatalogue
{
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _families = [];

    /// <param name="ids">Exact probe ids, or family prefixes that end in a dot.</param>
    public ModuleProbeCatalogue(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (string id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (id.EndsWith('.'))
            {
                _families.Add(id);
            }
            else
            {
                _exact.Add(id);
            }
        }
    }

    /// <summary>True when <paramref name="probeId"/> is an exact entry, or extends a family prefix by at least one character.</summary>
    public bool Contains(string probeId)
    {
        if (_exact.Contains(probeId))
        {
            return true;
        }

        foreach (string family in _families)
        {
            if (probeId.Length > family.Length && probeId.StartsWith(family, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
