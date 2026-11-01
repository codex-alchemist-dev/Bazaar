using System.Text.Json;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Settings;

/// <summary>The verdict of one floor check: allowed, or refused with the reason.</summary>
/// <param name="Allowed">True when the proposed value does not loosen a floor.</param>
/// <param name="Reason">Why it was refused; null when allowed.</param>
public sealed record FloorCheck(bool Allowed, string? Reason)
{
    public static FloorCheck Allow { get; } = new(true, null);

    public static FloorCheck Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The fixed floors from doc 15 that no <c>security.*</c> setting may loosen. A floor is not a setting: nothing here can be
/// changed by a write. Settings may tighten; a value that loosens a floor is refused before it reaches storage.
/// </summary>
/// <remarks>
/// Floors are absolute, not relative to the stored value, so an unchanged value is allowed whenever it passes its floor.
/// That is why <see cref="Check"/> takes no current value. Unknown <c>security.*</c> names are refused.
/// </remarks>
public static class SecurityFloors
{
    /// <summary>Longest task-scoped approval, in minutes. Doc 15 forbids a blanket scope, so every scope is bounded. The value is a Claude choice; the owner can revise it.</summary>
    public const int MaxTaskScopeMinutes = 480;

    /// <summary>Merges stay at this approval tier or stricter (doc 15 Tier 2).</summary>
    public const int MergeTier = 2;

    /// <summary>Highest tier number a setting may name. Anything above is not a tier this build can enforce.</summary>
    public const int HighestTier = 3;

    /// <summary>Tier 3 actions that can never leave <c>security.tier3.actions</c>.</summary>
    public static IReadOnlyList<Tier3Action> RequiredTier3Actions { get; } = [Tier3Action.Passkeys, Tier3Action.RecoveryCodes];

    /// <summary>Checks one proposed write to a <c>security.*</c> key. Unknown keys are refused.</summary>
    public static FloorCheck Check(string key, JsonElement proposed)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (IsCredentialMount(key))
            return FloorCheck.Refuse($"'{key}' would mount credentials; credentials are never mountable (doc 15 floor).");

        if (!SecuritySettings.IsKnown(key))
            return FloorCheck.Refuse($"'{key}' is not a security setting in the doc 15 settings table.");

        return key switch
        {
            "security.approval.tier2_when_untainted" => CheckTier2WhenUntainted(proposed),
            "security.tier3.actions" => CheckTier3Actions(proposed),
            "security.push.own_branch_untainted_tier" => CheckOwnBranchTier(proposed),
            "security.approval.task_scope_minutes" => CheckTaskScope(proposed),
            _ => FloorCheck.Allow,
        };
    }

    /// <summary>
    /// Floor (a): the hard-taint gate always applies. Both enum values keep it (the gate is in the engine, not a setting),
    /// so the only thing to check is that the value is one of them.
    /// </summary>
    private static FloorCheck CheckTier2WhenUntainted(JsonElement proposed)
    {
        if (!TryDeserialize<Tier2WhenUntainted>(proposed, out _))
            return FloorCheck.Refuse("security.approval.tier2_when_untainted must be always or hard_taint_only.");
        return FloorCheck.Allow;
    }

    /// <summary>Floor (b): passkeys and recovery codes stay in the Tier 3 list. Adding actions is fine.</summary>
    private static FloorCheck CheckTier3Actions(JsonElement proposed)
    {
        if (!TryDeserialize<List<Tier3Action>>(proposed, out var actions))
            return FloorCheck.Refuse("security.tier3.actions must be a list of Tier 3 action names.");

        foreach (var required in RequiredTier3Actions)
        {
            if (!actions!.Contains(required))
                return FloorCheck.Refuse($"security.tier3.actions must keep '{required}' (doc 15 floor).");
        }

        return FloorCheck.Allow;
    }

    /// <summary>
    /// Floor (c): merges stay Tier 2 or stricter. This key governs untainted own-branch pushes only and never sets the merge
    /// tier, which is <see cref="MergeTier"/> in code. The check refuses any value that is not a tier this build knows.
    /// </summary>
    private static FloorCheck CheckOwnBranchTier(JsonElement proposed)
    {
        if (!TryInt(proposed, out var tier) || tier is < 0 or > HighestTier)
            return FloorCheck.Refuse($"security.push.own_branch_untainted_tier must be a tier from 0 to {HighestTier}; merges stay Tier {MergeTier}.");
        return FloorCheck.Allow;
    }

    /// <summary>Floor (e): the task scope has a maximum, so it cannot become a blanket approval.</summary>
    private static FloorCheck CheckTaskScope(JsonElement proposed)
    {
        if (!TryInt(proposed, out var minutes) || minutes is < 1 or > MaxTaskScopeMinutes)
            return FloorCheck.Refuse($"security.approval.task_scope_minutes must be from 1 to {MaxTaskScopeMinutes} (no blanket scope).");
        return FloorCheck.Allow;
    }

    /// <summary>Floor (d): no setting may enable mounting credentials. No such key exists; any name that looks like one is refused.</summary>
    private static bool IsCredentialMount(string key) =>
        key.Contains("mount", StringComparison.OrdinalIgnoreCase)
        && (key.Contains("credential", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase));

    private static bool TryInt(JsonElement json, out int value)
    {
        value = 0;
        return json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out value);
    }

    private static bool TryDeserialize<T>(JsonElement json, out T? value)
    {
        value = default;
        try
        {
            value = json.Deserialize<T>(SettingJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }

        return value is not null;
    }
}
