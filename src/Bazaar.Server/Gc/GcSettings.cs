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

    /// <summary>Every <c>gc.*</c> key this build reads.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [InvitePolicyKey, EditWindowHoursKey];
}
