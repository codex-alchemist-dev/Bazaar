using Bazaar.Server.Calendar;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Calendar;

public sealed class CalendarSettingsTests
{
    [Fact]
    public void Known_registry_holds_the_three_calendar_keys_from_doc_24()
    {
        var names = SettingRegistry.Known.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("calendar.vacation_confirmer", names);
        Assert.Contains("calendar.release_creators", names);
        Assert.Contains("calendar.display_timezone", names);
        Assert.True(SettingRegistry.Known.TryGet("calendar.vacation_confirmer", out var confirmer));
        Assert.True(confirmer!.Scope == SettingScope.Project && confirmer.ApprovalTier == ApprovalTier.Tier2);
    }

    [Fact]
    public void Calendar_defaults_match_the_doc_table_and_the_policy_defaults()
    {
        Assert.Equal("\"owner\"", CalendarSettings.VacationConfirmerKey.DefaultValueJson);
        Assert.Equal("\"owner_or_project_lead\"", CalendarSettings.ReleaseCreatorsKey.DefaultValueJson);
        Assert.Equal("\"viewer\"", CalendarSettings.DisplayTimezoneKey.DefaultValueJson);

        var policy = new CalendarPolicy();
        Assert.Equal(VacationConfirmer.Owner, policy.VacationConfirmer);
        Assert.Equal(ReleaseCreators.OwnerOrProjectLead, policy.ReleaseCreators);
    }

    [Fact]
    public void Host_registry_is_unchanged_by_the_calendar_keys()
    {
        Assert.DoesNotContain(SettingRegistry.Host.Definitions, d => d.Name.StartsWith("calendar.", StringComparison.Ordinal));
    }
}
