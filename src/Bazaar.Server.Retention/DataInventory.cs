namespace Bazaar.Server.Retention;

/// <summary>
/// Merges the entries every module contributes and refuses an inconsistent inventory: an empty store name, a duplicate
/// category and store pair, or a personal-data entry whose category has no retention row. The check runs when the inventory
/// is built, so the host stops at start-up instead of keeping unlisted personal data (doc 32 section 9).
/// </summary>
public sealed class DataInventory : IDataInventory
{
    public DataInventory(IEnumerable<IDataInventoryContributor> contributors, IRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(policy);
        var seen = new HashSet<(string, string)>();
        var entries = new List<DataInventoryEntry>();
        foreach (var entry in contributors.SelectMany(c => c.Entries))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Store);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Category);
            if (!seen.Add((entry.Category, entry.Store)))
                throw new InvalidOperationException($"Data inventory lists '{entry.Store}' twice under '{entry.Category}'.");
            if (entry.ContainsPersonalData && policy.Find(entry.Category) is null)
                throw new InvalidOperationException($"Data inventory category '{entry.Category}' ('{entry.Store}') has no retention row.");
            entries.Add(entry);
        }

        Entries = entries.OrderBy(e => e.Category, StringComparer.Ordinal).ThenBy(e => e.Store, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<DataInventoryEntry> Entries { get; }
}
