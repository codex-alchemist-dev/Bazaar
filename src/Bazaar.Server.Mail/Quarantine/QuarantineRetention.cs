using Bazaar.Erasure.Abstractions;

namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// Which held items are due for raw-content purge (doc 22 setting <c>mail.quarantine.raw_retention_days</c>, doc 48 section 9).
/// Pure: the caller supplies the set of people under legal hold. The retention job calls this, then
/// <see cref="IMailQuarantineStore.TransitionAsync"/> per due item; the job mechanism itself is not defined here.
/// </summary>
public static class QuarantineRetention
{
    /// <summary>The shipped default for <c>mail.quarantine.raw_retention_days</c>. Needs counsel (doc 22, Conflicts 3).</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>When an item becomes due: its explicit <see cref="QuarantineItem.RetentionUntil"/>, else received time plus the retention days.</summary>
    public static DateTimeOffset DueAt(QuarantineItem item, int retentionDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        return item.RetentionUntil ?? item.ReceivedAt.AddDays(retentionDays);
    }

    /// <summary>True from the due instant on (the boundary is inclusive).</summary>
    public static bool IsDue(QuarantineItem item, int retentionDays, DateTimeOffset now) => now >= DueAt(item, retentionDays);

    /// <summary>
    /// Held items due for purge. An item whose person is under legal hold is never returned, however old it is. Items in any
    /// other state are ignored.
    /// </summary>
    public static IReadOnlyList<QuarantineItem> DueForPurge(
        IEnumerable<QuarantineItem> items, int retentionDays, DateTimeOffset now, IReadOnlySet<Guid> personsUnderHold)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(personsUnderHold);
        return items
            .Where(i => i.State == QuarantineState.Held && IsDue(i, retentionDays, now) && !personsUnderHold.Contains(i.PersonId))
            .OrderBy(i => i.ReceivedAt)
            .ThenBy(i => i.Id)
            .ToList();
    }

    /// <summary>Same as <see cref="DueForPurge"/>, asking <see cref="ILegalHoldReader"/> about each distinct person once.</summary>
    public static async Task<IReadOnlyList<QuarantineItem>> DueForPurgeAsync(
        IEnumerable<QuarantineItem> items, int retentionDays, DateTimeOffset now, ILegalHoldReader holds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(holds);
        var list = items.ToList();
        var held = new HashSet<Guid>();
        foreach (var person in list.Where(i => i.State == QuarantineState.Held && IsDue(i, retentionDays, now)).Select(i => i.PersonId).Distinct())
        {
            if (await holds.IsPersonHeldAsync(person, ct).ConfigureAwait(false))
            {
                held.Add(person);
            }
        }

        return DueForPurge(list, retentionDays, now, held);
    }
}
