using Bazaar.Diagnostics.Consent;

namespace Bazaar.Diagnostics.Tests.Consent;

public class ConsentChangeRecordBuilderTests
{
    private static readonly DateTimeOffset _when = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_a_record_with_every_audit_field()
    {
        var result = ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.Mode, "ask", "off", "Pausing reports during the release freeze");

        Assert.True(result.Succeeded);
        Assert.Null(result.Problem);
        Assert.Equal(
            new ConsentChangeRecord("operator-1", _when, ConsentSettingKeys.Mode, "ask", "off", "Pausing reports during the release freeze"),
            result.Record);
    }

    [Fact]
    public void Time_is_stored_in_utc()
    {
        var local = new DateTimeOffset(2026, 11, 13, 11, 30, 0, TimeSpan.FromHours(2));

        var result = ConsentChangeRecordBuilder.Build("operator-1", local, ConsentSettingKeys.Mode, "ask", "off", "reason");

        Assert.Equal(_when, result.Record!.When);
        Assert.Equal(TimeSpan.Zero, result.Record.When.Offset);
    }

    [Fact]
    public void Who_key_and_reason_are_trimmed()
    {
        var result = ConsentChangeRecordBuilder.Build("  operator-1 ", _when, $" {ConsentSettingKeys.Mode} ", "ask", "off", "  why  ");

        Assert.Equal("operator-1", result.Record!.Who);
        Assert.Equal(ConsentSettingKeys.Mode, result.Record.Key);
        Assert.Equal("why", result.Record.Reason);
    }

    [Fact]
    public void Values_are_stored_as_given()
    {
        var result = ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.IssueRepo, "", " owner/name", "filing on");

        Assert.Equal(string.Empty, result.Record!.OldValue);
        Assert.Equal(" owner/name", result.Record.NewValue);
    }

    [Fact]
    public void Refuses_a_no_op_change()
    {
        var result = ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.Mode, "ask", "ask", "reason");

        Assert.False(result.Succeeded);
        Assert.Null(result.Record);
        Assert.Contains("no-op", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void No_op_comparison_is_ordinal()
    {
        var result = ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.IssueRepo, "owner/name", "Owner/name", "reason");

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_a_blank_who(string who)
    {
        var result = ConsentChangeRecordBuilder.Build(who, _when, ConsentSettingKeys.Mode, "ask", "off", "reason");

        Assert.False(result.Succeeded);
        Assert.Contains("actor", result.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Refuses_a_blank_reason(string reason)
    {
        var result = ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.Mode, "ask", "off", reason);

        Assert.False(result.Succeeded);
        Assert.Contains("reason", result.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("telemetry.crash.unknown")]
    [InlineData("security.audit.export_target")]
    [InlineData("")]
    public void Refuses_a_key_that_is_not_a_crash_report_setting(string key)
    {
        var result = ConsentChangeRecordBuilder.Build("operator-1", _when, key, "a", "b", "reason");

        Assert.False(result.Succeeded);
        Assert.Contains("not a crash-report setting key", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_registered_key_is_accepted()
    {
        foreach (var key in ConsentSettingKeys.All)
        {
            var result = ConsentChangeRecordBuilder.Build("operator-1", _when, key, "a", "b", "reason");

            Assert.True(result.Succeeded, key);
        }
    }

    [Fact]
    public void Registry_key_list_has_thirteen_unique_entries()
    {
        Assert.Equal(13, ConsentSettingKeys.All.Count);
        Assert.Equal(13, ConsentSettingKeys.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Null_old_or_new_value_is_rejected_with_an_exception()
    {
        Assert.Throws<ArgumentNullException>(() => ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.Mode, null!, "off", "reason"));
        Assert.Throws<ArgumentNullException>(() => ConsentChangeRecordBuilder.Build("operator-1", _when, ConsentSettingKeys.Mode, "ask", null!, "reason"));
    }
}
