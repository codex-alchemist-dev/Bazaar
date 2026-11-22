namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// A dated price table. <see cref="Version"/> is the <c>price_version</c> stored on usage rows. For a call at a given
/// time, <see cref="Lookup"/> picks the entry whose effective date is the latest one on or before that UTC date.
/// </summary>
public sealed class PriceTable
{
    private readonly Dictionary<(string ProviderId, string ModelId), List<PriceEntry>> _byModel = new();

    /// <summary>Builds the table. A second entry for the same provider, model and date is rejected.</summary>
    public PriceTable(string version, IEnumerable<PriceEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(entries);

        Version = version;
        var seen = new HashSet<(string ProviderId, string ModelId, DateOnly EffectiveFrom)>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!seen.Add((entry.ProviderId, entry.ModelId, entry.EffectiveFrom)))
            {
                throw new ArgumentException(
                    $"Duplicate price for {entry.ProviderId}:{entry.ModelId} effective {entry.EffectiveFrom:yyyy-MM-dd}.",
                    nameof(entries));
            }

            var key = (entry.ProviderId, entry.ModelId);
            if (!_byModel.TryGetValue(key, out var list))
            {
                list = [];
                _byModel[key] = list;
            }

            list.Add(entry);
        }

        foreach (var list in _byModel.Values)
        {
            list.Sort((a, b) => a.EffectiveFrom.CompareTo(b.EffectiveFrom));
        }
    }

    /// <summary>The <c>price_version</c> written on usage rows priced from this table.</summary>
    public string Version { get; }

    /// <summary>
    /// The price in force for the model at <paramref name="at"/>, judged on its UTC date. Returns null when the model
    /// has no entry, or when <paramref name="at"/> falls before its first entry.
    /// </summary>
    public PriceEntry? Lookup(string providerId, string modelId, DateTimeOffset at)
    {
        if (!_byModel.TryGetValue((providerId, modelId), out var list))
        {
            return null;
        }

        var day = DateOnly.FromDateTime(at.UtcDateTime);
        PriceEntry? inForce = null;
        foreach (var entry in list)
        {
            if (entry.EffectiveFrom > day)
            {
                break;
            }

            inForce = entry;
        }

        return inForce;
    }
}
