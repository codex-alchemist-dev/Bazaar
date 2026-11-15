namespace Bazaar.Client.Core.Layout;

/// <summary>Result of merging a person's layout over the company's. Issues list rejected person slots.</summary>
public sealed record LayoutMergeResult(LayoutDocument Effective, IReadOnlyList<LayoutIssue> Issues);

/// <summary>
/// Applies a person's layout over the company layout (doc 41 section 10, decisions 4 and 6). A person may replace a slot
/// only when the company has not locked it. Only the owner may override a lock. A person's own locks are ignored, so
/// nobody below the owner can change which slots are locked.
/// </summary>
public static class LayoutScopeMerger
{
    public static LayoutMergeResult Merge(LayoutDocument company, LayoutDocument? person, bool actorIsOwner)
    {
        ArgumentNullException.ThrowIfNull(company);

        if (person is null)
        {
            return new LayoutMergeResult(company, []);
        }

        var issues = new List<LayoutIssue>();
        var slots = new List<LayoutSlot>(company.Slots);

        for (int i = 0; i < person.Slots.Count; i++)
        {
            LayoutSlot slot = person.Slots[i];
            if (!actorIsOwner && LayoutResolver.IsLocked(company.Locks, slot))
            {
                issues.Add(new LayoutIssue($"/slots/{i}", "locked-slot",
                    $"The company locks {LayoutResolver.KeyOf(slot)}. Only the owner can override it."));
                continue;
            }

            int existing = slots.FindIndex(s => SameKey(s, slot));
            if (existing >= 0)
            {
                slots[existing] = slot;
            }
            else
            {
                slots.Add(slot);
            }
        }

        var effective = company with { Slots = slots };
        return new LayoutMergeResult(effective, issues);
    }

    private static bool SameKey(LayoutSlot a, LayoutSlot b) =>
        a.DeviceClass == b.DeviceClass && a.Display == b.Display && a.Posture == b.Posture;
}
