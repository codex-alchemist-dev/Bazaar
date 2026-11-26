using Bazaar.Server.Gc.Members;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Gc;

/// <summary>
/// The <c>gc.*</c> operator settings from docs/design/01 (settings table). Defaults match the doc. Both are install-scoped and
/// set by the operator; the store audits every change.
/// </summary>
public static class GcSettings
{
    /// <summary>
    /// <c>gc.invite_policy</c>. The wire form is the enum's snake_case name (<c>any_member</c>, <c>owner_only</c>), as the
    /// settings registry writes every enum.
    /// </summary>
    public static readonly SettingKey<GcInvitePolicy> InvitePolicyKey = new(
        "gc.invite_policy", GcInvitePolicy.AnyMember, SettingScope.Install, SettingRole.Operator,
        "Who may invite a principal into a GC: any active member (always logged), or the owner only.");

    /// <summary>
    /// <c>gc.edit_window_hours</c>. Hours after a message is written during which its author may edit it. 0 means unlimited.
    /// </summary>
    public static readonly SettingKey<int> EditWindowHoursKey = new(
        "gc.edit_window_hours", 0, SettingScope.Install, SettingRole.Operator,
        "Hours after a message is written during which its author may edit it. 0 means unlimited.",
        validate: hours => hours < 0 ? "must be 0 (unlimited) or more" : null);

    /// <summary>Setting <c>gc.unarchive_policy</c>: who may unarchive a GC. Owner by default.</summary>
    public static readonly SettingKey<GcUnarchivePolicy> UnarchivePolicyKey = new(
        "gc.unarchive_policy", GcUnarchivePolicy.Owner, SettingScope.Install, SettingRole.Operator,
        "Who may unarchive a GC: the owner (the default), or any active member.");

    /// <summary>Setting <c>gc.default_gc_rule</c> [PROPOSED, doc 02, GC list]: how <c>gc.list</c> picks the caller's default GC.</summary>
    public static readonly SettingKey<Wire.GcDefaultRule> DefaultGcRuleKey = new(
        "gc.default_gc_rule", Wire.GcDefaultRule.MostRecentlyActive, SettingScope.Install, SettingRole.Operator,
        "How gc.list picks the default GC: the most recently active ordinary GC (the default), or none.");

    /// <summary>Setting <c>gc.dm_access_approvers</c>. The owner is always an approver; the change needs owner approval.</summary>
    public static readonly SettingKey<GcDmAccessApprovers> DmAccessApproversKey = new(
        "gc.dm_access_approvers", GcDmAccessApprovers.Owner, SettingScope.Install, SettingRole.Operator,
        "Who may approve a DM access request: the owner (the default), or the owner and the DM's members.",
        approvalTier: ApprovalTier.Tier2);

    /// <summary>Setting <c>gc.broadcast_mentions</c>: who may use <c>@here</c> and <c>@all</c>.</summary>
    public static readonly SettingKey<GcBroadcastMentions> BroadcastMentionsKey = new(
        "gc.broadcast_mentions", GcBroadcastMentions.OwnerAndAgentsRateLimited, SettingScope.Install, SettingRole.Operator,
        "Who may use @here and @all: the owner only, or the owner and agents within the rate limit (the default).");

    /// <summary>Setting <c>gc.broadcast_mention_rate_per_hour</c>. Broadcast mentions an agent may post per hour.</summary>
    public static readonly SettingKey<int> BroadcastMentionRatePerHourKey = new(
        "gc.broadcast_mention_rate_per_hour", 2, SettingScope.Install, SettingRole.Operator,
        "Broadcast mentions (@here, @all) an agent may post per hour.",
        validate: rate => rate < 0 ? "must be 0 or more" : null);

    /// <summary>Setting <c>gc.vault_embed_pin</c>: which revision a Vault embed shows.</summary>
    public static readonly SettingKey<GcVaultEmbedPin> VaultEmbedPinKey = new(
        "gc.vault_embed_pin", GcVaultEmbedPin.Commit, SettingScope.Install, SettingRole.Operator,
        "Which revision a Vault embed shows: the commit it was made at (the default), or the branch head.");

    /// <summary>Setting <c>gc.message_max_bytes</c>. The largest accepted message body, in bytes.</summary>
    public static readonly SettingKey<int> MessageMaxBytesKey = new(
        "gc.message_max_bytes", 32768, SettingScope.Install, SettingRole.Operator,
        "The largest accepted message body, in bytes.",
        validate: bytes => bytes <= 0 ? "must be more than 0" : null);

    /// <summary>Setting <c>gc.blob_max_bytes</c>. The largest accepted blob upload, in bytes.</summary>
    public static readonly SettingKey<int> BlobMaxBytesKey = new(
        "gc.blob_max_bytes", 26214400, SettingScope.Install, SettingRole.Operator,
        "The largest accepted blob upload, in bytes (25 MiB by default).",
        validate: bytes => bytes <= 0 ? "must be more than 0" : null);

    /// <summary>
    /// Setting <c>privacy.owner_dm_read_marker</c> (docs/design/32, Settings). When on, a DM shows its members that the owner can read it.
    /// Turning it off needs owner approval and counsel review (doc 32); the key sits here with the other DM rules.
    /// </summary>
    public static readonly SettingKey<bool> OwnerDmReadMarkerKey = new(
        "privacy.owner_dm_read_marker", true, SettingScope.Install, SettingRole.Owner,
        "Show DM members that the owner can read the DM. On by default; turning it off needs the owner's approval.",
        approvalTier: ApprovalTier.Tier2,
        requiresApprovalFor: shown => !shown);

    /// <summary>Every <c>gc.*</c> key this build reads, plus <c>privacy.owner_dm_read_marker</c>.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        InvitePolicyKey, EditWindowHoursKey, UnarchivePolicyKey, DefaultGcRuleKey,DmAccessApproversKey, BroadcastMentionsKey,
        BroadcastMentionRatePerHourKey, VaultEmbedPinKey, MessageMaxBytesKey, BlobMaxBytesKey, OwnerDmReadMarkerKey,
    ];
}
