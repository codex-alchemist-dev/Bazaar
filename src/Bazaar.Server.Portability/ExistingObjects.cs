namespace Bazaar.Server.Portability;

/// <summary>
/// An object already stored for the target, found by kind and title. <see cref="Version"/> is the revision the store
/// reports for it (for the Vault, the HEAD commit it was read at); a replace records it in the audit.
/// </summary>
public sealed record ExistingObject(string Kind, string Title, string ObjectId, string Version)
{
    /// <summary>The GC author of the stored message, as a GUID string. Set by the GC index; a GC replace needs the same author.</summary>
    public string? Owner { get; init; }

    /// <summary>The Vault path of the stored note, relative to the checkout. Set by the Vault index; a Vault replace writes there.</summary>
    public string? Location { get; init; }
}

/// <summary>An incoming record that has the same kind and title as an existing object (doc 44 section 8.10).</summary>
public sealed record ExistingCollision(SourceRecord Incoming, ExistingObject Existing);

/// <summary>Looks up stored objects by kind and title. The host wires one per target (Vault, GC).</summary>
public interface IExistingObjectIndex
{
    Task<IReadOnlyList<ExistingObject>> FindAsync(IReadOnlyList<(string Kind, string Title)> wanted, CancellationToken ct);
}

/// <summary>Pure matching of incoming records against existing objects. Empty kinds and titles never match.</summary>
public static class ExistingCollisions
{
    public static IReadOnlyList<ExistingCollision> Find(ImportPreview preview, IReadOnlyList<ExistingObject> existing)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(existing);

        var byKey = new Dictionary<(string, string), ExistingObject>();
        foreach (var item in existing)
        {
            byKey.TryAdd((item.Kind, item.Title), item);
        }

        var found = new List<ExistingCollision>();
        foreach (var record in preview.New)
        {
            if (record.Kind.Length == 0 || record.Title.Length == 0)
            {
                continue;
            }
            if (byKey.TryGetValue((record.Kind, record.Title), out var match))
            {
                found.Add(new ExistingCollision(record, match));
            }
        }
        return found;
    }

    /// <summary>The wanted (kind, title) pairs of a preview's new records, for <see cref="IExistingObjectIndex"/>.</summary>
    public static IReadOnlyList<(string Kind, string Title)> WantedFor(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview.New
            .Where(r => r.Kind.Length > 0 && r.Title.Length > 0)
            .Select(r => (r.Kind, r.Title))
            .Distinct()
            .ToList();
    }
}
