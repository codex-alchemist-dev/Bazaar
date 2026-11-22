using System.Text.Json;

namespace Bazaar.Server.Settings;

/// <summary>
/// A registered operator setting, typed by its subclass <see cref="SettingKey{T}"/>. Keys are declared in code; the
/// database holds only values. Evaluation is pure: it never touches storage.
/// </summary>
public abstract class SettingDefinition
{
    protected SettingDefinition(string name, SettingScope scope, SettingRole whoMayChange, ApprovalTier approvalTier, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        Name = name;
        Scope = scope;
        WhoMayChange = whoMayChange;
        ApprovalTier = approvalTier;
        Description = description;
    }

    /// <summary>Dotted, area-prefixed key, for example <c>host.status.bind</c>.</summary>
    public string Name { get; }

    public SettingScope Scope { get; }

    /// <summary>Roles allowed to write this key. An actor needs at least one of them.</summary>
    public SettingRole WhoMayChange { get; }

    /// <summary>Approval tier for a change. <see cref="ApprovalTier.None"/> means applied at once when valid.</summary>
    public ApprovalTier ApprovalTier { get; }

    public string Description { get; }

    /// <summary>The CLR type of the value (bool, int, string, an enum, or a read-only list).</summary>
    public abstract Type ValueType { get; }

    /// <summary>The default value as stored JSON text (what a read returns when the key is unset).</summary>
    public abstract string DefaultValueJson { get; }

    /// <summary>Runs the type check, the validator (which also carries the floors) and the approval rule for a proposed write.</summary>
    public abstract SettingEvaluation Evaluate(SettingActor actor, JsonElement proposed);

    /// <summary>Type and validator check for a value read from storage. Returns null when it is valid, else the reason.</summary>
    public abstract string? CheckValue(JsonElement value);
}

/// <summary>What the registry decided about one proposed write, before anything is stored.</summary>
/// <param name="Outcome">Accepted, PendingApproval or Rejected.</param>
/// <param name="ApprovalTier">The approval the change needs; None unless PendingApproval.</param>
/// <param name="Reason">Why it was rejected or held; null when accepted.</param>
/// <param name="ValueJson">Canonical JSON of the proposed value, or the raw text when it did not parse.</param>
public sealed record SettingEvaluation(SettingWriteOutcome Outcome, ApprovalTier ApprovalTier, string? Reason, string ValueJson);
