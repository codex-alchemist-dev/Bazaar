namespace Bazaar.Server.Setup;

/// <summary>What a plan would do to one leaf value (doc 45 section 1.7, dry-run).</summary>
public enum SetupChangeKind
{
    /// <summary>The plan sets a value that the current state does not have.</summary>
    Create,

    /// <summary>The plan sets a different value from the current one.</summary>
    Update,

    /// <summary>The current state has a value that the plan does not set.</summary>
    Remove,
}

/// <summary>One change. The values are canonical JSON text, and only references appear, never secrets.</summary>
/// <param name="Path">Dotted leaf path, for example <c>install.companyName</c> or <c>settings.host.shutdown_timeout_seconds</c>.</param>
/// <param name="Kind">Create, Update or Remove.</param>
/// <param name="CurrentValue">The canonical text of the current value; null for Create.</param>
/// <param name="PlannedValue">The canonical text of the planned value; null for Remove.</param>
public sealed record SetupChange(string Path, SetupChangeKind Kind, string? CurrentValue, string? PlannedValue);

/// <summary>The current state, as canonical leaf values keyed by the same paths the plan uses. An empty snapshot is an empty install.</summary>
public sealed class SetupStateSnapshot
{
    public SetupStateSnapshot(IReadOnlyDictionary<string, string> leafValues)
    {
        ArgumentNullException.ThrowIfNull(leafValues);
        LeafValues = leafValues;
    }

    /// <summary>Canonical text of each leaf, keyed by dotted path.</summary>
    public IReadOnlyDictionary<string, string> LeafValues { get; }

    /// <summary>The state of an install that has nothing yet, so every planned leaf is a create.</summary>
    public static SetupStateSnapshot Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Builds a snapshot from a JSON node, with the same flattening the plan uses.</summary>
    public static SetupStateSnapshot FromNode(System.Text.Json.Nodes.JsonNode? node) =>
        new(SetupPlanCanonicalizer.Leaves(node));
}

