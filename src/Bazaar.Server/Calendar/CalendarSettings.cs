using Bazaar.Server.Settings;

namespace Bazaar.Server.Calendar;

/// <summary>Which client a viewer sees calendar times in (doc 24 <c>calendar.display_timezone</c>).</summary>
public enum DisplayTimezone
{
    Viewer,
    Agent,
}

/// <summary>
/// The <c>calendar.*</c> settings from doc 24 ("MOVED TO SETTINGS" and the settings table). Defaults match the doc. Changes
/// are audited like every setting. <c>calendar.vacation_confirmer</c> needs owner approval (Tier 2), as the doc table says.
/// </summary>
public static class CalendarSettings
{
    public static readonly SettingKey<VacationConfirmer> VacationConfirmerKey = new(
        "calendar.vacation_confirmer", VacationConfirmer.Owner, SettingScope.Project, SettingRole.Owner,
        "Who confirms a vacation request: the owner, or the project lead.", ApprovalTier.Tier2);

    public static readonly SettingKey<ReleaseCreators> ReleaseCreatorsKey = new(
        "calendar.release_creators", ReleaseCreators.OwnerOrProjectLead, SettingScope.Project, SettingRole.Owner,
        "Who may create release and milestone events: the owner, or the owner or project lead.");

    public static readonly SettingKey<DisplayTimezone> DisplayTimezoneKey = new(
        "calendar.display_timezone", DisplayTimezone.Viewer, SettingScope.Install, SettingRole.Operator,
        "Timezone for calendar times: the viewer's own, or the agent's. Each viewer may choose this on their client.");

    public static IReadOnlyList<SettingDefinition> All { get; } =
        [VacationConfirmerKey, ReleaseCreatorsKey, DisplayTimezoneKey];
}
