namespace Bazaar.Portability.Client;

/// <summary>
/// Asks every target's index (Vault, GC) and returns all the stored objects found. The host passes one index per target, so a
/// record is checked against both stores whatever its target kind.
/// </summary>
public sealed class CompositeExistingObjectIndex : IExistingObjectIndex
{
    private readonly IReadOnlyList<IExistingObjectIndex> _indexes;

    public CompositeExistingObjectIndex(IEnumerable<IExistingObjectIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        _indexes = indexes.ToList();
    }

    public async Task<IReadOnlyList<ExistingObject>> FindAsync(IReadOnlyList<(string Kind, string Title)> wanted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var found = new List<ExistingObject>();
        foreach (var index in _indexes)
        {
            found.AddRange(await index.FindAsync(wanted, ct));
        }
        return found;
    }
}
