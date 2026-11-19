namespace Bazaar.Server.Retention;

/// <summary>How a category's data is removed when its period ends (design doc 48 section 9).</summary>
public enum ErasureMethod
{
    /// <summary>Never removed by retention. The GC log is in this class: only its personal fields leave, by crypto-erase.</summary>
    None = 0,

    /// <summary>Soft delete first, then purge after the soft-delete window.</summary>
    SoftDeleteThenPurge = 1,

    /// <summary>Destroy the per-subject key and append a tombstone; structure, timestamps and authorship stay.</summary>
    CryptoErase = 2,
}

/// <summary>What retention says to do with one record right now.</summary>
public enum RetentionDisposition
{
    /// <summary>Inside its retention period, or the category is kept.</summary>
    Keep = 0,

    /// <summary>Past its period: mark it with a tombstone and hide it (restorable until the window ends).</summary>
    SoftDeleteDue = 1,

    /// <summary>Soft-deleted and past the window: it may be removed for good.</summary>
    PurgeDue = 2,

    /// <summary>A legal hold covers it, so removal is blocked and queued.</summary>
    Held = 3,
}

/// <summary>
/// One row of the per-category retention table (doc 48 section 9). Periods are owner settings; a null
/// <see cref="Period"/> means keep.
/// </summary>
/// <param name="Category">The data category id, the same ids as the data inventory.</param>
/// <param name="Period">How long a record is kept after it was created. Null keeps it.</param>
/// <param name="SoftDeleteWindow">How long a soft-deleted record stays restorable before it may be purged.</param>
/// <param name="Method">How the category is removed.</param>
public sealed record RetentionCategory(string Category, TimeSpan? Period, TimeSpan SoftDeleteWindow, ErasureMethod Method);

/// <summary>
/// One entry of the data inventory (doc 32 section 9): a store that can hold personal data, with its purpose and category.
/// </summary>
/// <param name="Category">The retention category the data belongs to.</param>
/// <param name="Store">The table, file area or index, for example <c>gc_message</c>.</param>
/// <param name="Fields">The fields that can hold personal data. Empty means the whole store.</param>
/// <param name="Purpose">Why the data is kept, in one line.</param>
/// <param name="ContainsPersonalData">False for stores listed only to show they were reviewed.</param>
public sealed record DataInventoryEntry(string Category, string Store, IReadOnlyList<string> Fields, string Purpose, bool ContainsPersonalData = true);

/// <summary>A module contributes the inventory entries for the stores it owns.</summary>
public interface IDataInventoryContributor
{
    /// <summary>The entries this module owns.</summary>
    IReadOnlyList<DataInventoryEntry> Entries { get; }
}

/// <summary>The merged, validated data inventory.</summary>
public interface IDataInventory
{
    /// <summary>All entries, ordered by category then store.</summary>
    IReadOnlyList<DataInventoryEntry> Entries { get; }
}

/// <summary>The retention table.</summary>
public interface IRetentionPolicy
{
    /// <summary>The row for a category, or null when the category has none (which retention treats as keep).</summary>
    RetentionCategory? Find(string category);
}

/// <summary>Decides what to do with a record (doc 48 section 9). Pure: no clock, store or hold lookup of its own.</summary>
public interface IRetentionPlanner
{
    /// <summary>The disposition of a record of <paramref name="category"/>.</summary>
    /// <param name="category">The data category.</param>
    /// <param name="createdAt">When the record was created.</param>
    /// <param name="softDeletedAt">When it was soft-deleted, or null.</param>
    /// <param name="now">The current time.</param>
    /// <param name="underLegalHold">True when a legal hold covers the record.</param>
    RetentionDisposition Decide(string category, DateTimeOffset createdAt, DateTimeOffset? softDeletedAt, DateTimeOffset now, bool underLegalHold);
}
