using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Client.Layout;

/// <summary>Result of merging a person's layout over the company's. Issues list rejected person slots.</summary>
public sealed record LayoutMergeResult(LayoutDocument Effective, IReadOnlyList<LayoutIssue> Issues);

/// <summary>
/// Applies a person's layout over the company layout (doc 41 section 10, decisions 4 and 6). A person may replace a slot
/// only when the company has not locked it. Only the owner may override a lock. A person's own locks are ignored, so
/// nobody below the owner can change which slots are locked.
/// </summary>
/// <remarks>
/// Three lock forms are checked (see <see cref="LayoutVocabulary"/>). A person slot that breaks any of them is rejected
/// as a whole, with one issue per broken lock:
/// <list type="bullet">
/// <item><description><c>locked-slot</c>: the slot pattern is locked.</description></item>
/// <item><description><c>locked-node</c>: a locked node (by pane id) is removed, moved to another ancestor chain, or changed in any way.</description></item>
/// <item><description><c>locked-setting</c>: a top-level key of a locked pane's config is added, removed or changed.</description></item>
/// </list>
/// The company's copy of a locked node is its copy in the slot being replaced, or else its first occurrence in document
/// order. A locked node is protected in every slot the person sends, not only in the slot it replaces.
/// </remarks>
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
        Dictionary<string, Anchor>? anchors = null;

        for (int i = 0; i < person.Slots.Count; i++)
        {
            LayoutSlot slot = person.Slots[i];
            string path = $"/slots/{i}";
            if (!actorIsOwner)
            {
                if (LayoutResolver.IsLocked(company.Locks, slot))
                {
                    issues.Add(new LayoutIssue(path, "locked-slot",
                        $"The company locks {LayoutResolver.KeyOf(slot)}. Only the owner can override it."));
                    continue;
                }

                anchors ??= IndexCompany(company);
                List<LayoutIssue> violations = CheckLocks(company, slot, anchors, path);
                if (violations.Count > 0)
                {
                    issues.AddRange(violations);
                    continue;
                }
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

    /// <summary>A node as the company wrote it, with the pane ids of its ancestors (root first; null where a container has no id).</summary>
    private sealed record Anchor(LayoutNode Node, IReadOnlyList<string?> Ancestors);

    private static List<LayoutIssue> CheckLocks(LayoutDocument company, LayoutSlot slot, Dictionary<string, Anchor> anchors, string path)
    {
        var violations = new List<LayoutIssue>();
        LayoutSlot? companySlot = company.Slots.FirstOrDefault(s => SameKey(s, slot));

        foreach (string pattern in company.Locks.Distinct(StringComparer.Ordinal))
        {
            if (LayoutResolver.TryParseNodeLock(pattern, out string nodeId))
            {
                Anchor? expected = Expected(companySlot, nodeId, anchors, out Anchor? inSlot);
                if (expected is null)
                {
                    continue;
                }

                Anchor? actual = Find(slot.Root, nodeId, []);
                bool removed = actual is null && inSlot is not null;
                bool changed = actual is not null && !SameAnchor(actual, expected);
                if (removed || changed)
                {
                    violations.Add(new LayoutIssue(path, "locked-node",
                        $"The company locks node {nodeId}. Only the owner can change it."));
                }
            }
            else if (LayoutResolver.TryParseSettingLock(pattern, out string paneId, out string key))
            {
                Anchor? expected = Expected(companySlot, paneId, anchors, out Anchor? inSlot);
                if (expected is null)
                {
                    continue;
                }

                (bool canonicalHas, JsonNode? canonicalValue) = ConfigValue(expected.Node.PaneConfig, key);
                Anchor? actual = Find(slot.Root, paneId, []);
                bool changed;
                if (actual is null)
                {
                    changed = canonicalHas && inSlot is not null;
                }
                else
                {
                    (bool has, JsonNode? value) = ConfigValue(actual.Node.PaneConfig, key);
                    changed = has != canonicalHas || !JsonNode.DeepEquals(value, canonicalValue);
                }

                if (changed)
                {
                    violations.Add(new LayoutIssue(path, "locked-setting",
                        $"The company locks setting {key} of {paneId}. Only the owner can change it."));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The company's copy of a locked node for this slot: its copy in the replaced slot when there is one, otherwise its
    /// first occurrence anywhere in the company layout. <paramref name="inSlot"/> is the copy in the replaced slot only.
    /// </summary>
    private static Anchor? Expected(LayoutSlot? companySlot, string paneId, Dictionary<string, Anchor> anchors, out Anchor? inSlot)
    {
        inSlot = companySlot is null ? null : Find(companySlot.Root, paneId, []);
        return inSlot ?? (anchors.TryGetValue(paneId, out Anchor? first) ? first : null);
    }

    /// <summary>First occurrence of every pane id in the company layout, in document order.</summary>
    private static Dictionary<string, Anchor> IndexCompany(LayoutDocument company)
    {
        var index = new Dictionary<string, Anchor>(StringComparer.Ordinal);
        foreach (LayoutSlot slot in company.Slots)
        {
            Index(slot.Root, [], index);
        }

        return index;
    }

    private static void Index(LayoutNode node, IReadOnlyList<string?> ancestors, Dictionary<string, Anchor> index)
    {
        if (node.PaneId is not null)
        {
            index.TryAdd(node.PaneId, new Anchor(node, ancestors));
        }

        List<string?> childAncestors = [.. ancestors, node.PaneId];
        foreach (LayoutNode child in node.Children)
        {
            Index(child, childAncestors, index);
        }
    }

    /// <summary>First node with the pane id in document order, with its ancestors. Null when absent.</summary>
    private static Anchor? Find(LayoutNode node, string paneId, IReadOnlyList<string?> ancestors)
    {
        if (node.PaneId == paneId)
        {
            return new Anchor(node, ancestors);
        }

        List<string?> childAncestors = [.. ancestors, node.PaneId];
        foreach (LayoutNode child in node.Children)
        {
            Anchor? found = Find(child, paneId, childAncestors);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool SameAnchor(Anchor a, Anchor b) =>
        a.Ancestors.SequenceEqual(b.Ancestors) && SameNode(a.Node, b.Node);

    private static bool SameNode(LayoutNode a, LayoutNode b)
    {
        if (a.Container != b.Container || a.PaneKind != b.PaneKind || a.PaneId != b.PaneId || a.VisibleWhen != b.VisibleWhen || a.Children.Count != b.Children.Count)
        {
            return false;
        }

        if (!SameConfig(a.PaneConfig, b.PaneConfig))
        {
            return false;
        }

        for (int i = 0; i < a.Children.Count; i++)
        {
            if (!SameNode(a.Children[i], b.Children[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameConfig(JsonElement? a, JsonElement? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return JsonNode.DeepEquals(JsonNode.Parse(a.Value.GetRawText()), JsonNode.Parse(b.Value.GetRawText()));
    }

    /// <summary>Whether a top-level key exists in a pane config, and its value. A missing config has no keys.</summary>
    private static (bool Present, JsonNode? Value) ConfigValue(JsonElement? config, string key)
    {
        if (config is not JsonElement element || element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out JsonElement value))
        {
            return (false, null);
        }

        return (true, JsonNode.Parse(value.GetRawText()));
    }

    private static bool SameKey(LayoutSlot a, LayoutSlot b) =>
        a.DeviceClass == b.DeviceClass && a.Display == b.Display && a.Posture == b.Posture;
}
