using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The backups and email sections of the plan (doc 45 steps 8 and 11) against the real registry: shape, approval holds and duplicates.</summary>
public sealed class SetupStoresValidationTests
{
    private static SetupLoadResult Load(string json) => SetupPlanLoader.Load(json, RegistrySetupSettingCatalog.Known);

    private static string WithBackups(string backups) =>
        SetupFixtures.With("\"backups\": { \"target\": \"file:/mnt/backup/bazaar\", \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": false }", "\"backups\": " + backups);

    [Fact]
    public void The_valid_plan_has_no_issue_from_the_new_sections()
    {
        var result = Load(SetupFixtures.Valid);

        Assert.DoesNotContain(result.Issues, issue => issue.Path.StartsWith("backups", StringComparison.Ordinal) || issue.Path.StartsWith("email", StringComparison.Ordinal));
    }

    [Fact]
    public void An_offsite_target_is_held_for_owner_approval()
    {
        var result = Load(WithBackups("{ \"target\": \"s3:https://s3.example.invalid/bucket\", \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": false }"));

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Hold && issue.Path == "backups.target");
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("ftp:host/path")]
    [InlineData("file:/with space")]
    public void A_malformed_target_is_an_error(string target)
    {
        var result = Load(WithBackups("{ \"target\": \"" + target + "\", \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": false }"));

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "backups.target");
    }

    [Fact]
    public void A_target_with_credentials_is_an_error_that_does_not_echo_them()
    {
        var secret = string.Concat("pa", "55", "-fake");
        var result = Load(WithBackups("{ \"target\": \"s3:user:" + secret + "@host/bucket\", \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": false }"));

        var issue = Assert.Single(result.Issues, i => i.Path == "backups.target");
        Assert.Equal(SetupIssueSeverity.Error, issue.Severity);
        Assert.DoesNotContain(secret, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_target_with_the_acknowledgement_is_valid()
    {
        var result = Load(WithBackups("{ \"target\": null, \"drillIntervalDays\": 30, \"acknowledgeNoTarget\": true }"));

        Assert.DoesNotContain(result.Issues, issue => issue.Path.StartsWith("backups", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("backup.target", "\"file:/x\"", "backups.target")]
    [InlineData("backup.drill_interval_days", "7", "backups.drillIntervalDays")]
    [InlineData("backup.no_target_acknowledged", "true", "backups.acknowledgeNoTarget")]
    [InlineData("mail.enabled", "true", "email.enabled")]
    [InlineData("mail.backend", "\"cloudflare\"", "email.backend")]
    public void Setting_a_mapped_key_in_the_settings_map_too_is_one_error(string key, string value, string planPath)
    {
        var plan = SetupFixtures.With("\"settings\": { \"host.shutdown_timeout_seconds\": 20 }", "\"settings\": { \"" + key + "\": " + value + " }");

        var result = Load(plan);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "settings." + key && issue.Message.Contains("already set by " + planPath, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bazaar_client")]
    [InlineData("imap_smtp")]
    [InlineData("cloudflare")]
    [InlineData("builtin_server")]
    public void Email_on_with_a_known_back_end_is_valid(string backend)
    {
        var result = Load(SetupFixtures.With("\"email\": { \"enabled\": false }", "\"email\": { \"enabled\": true, \"backend\": \"" + backend + "\" }"));

        Assert.DoesNotContain(result.Issues, issue => issue.Path.StartsWith("email", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ \"enabled\": true }")]
    [InlineData("{ \"enabled\": true, \"backend\": \"gmail\" }")]
    public void Email_on_needs_a_known_back_end(string email)
    {
        var result = Load(SetupFixtures.With("\"email\": { \"enabled\": false }", "\"email\": " + email));

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "email.backend");
    }

    [Fact]
    public void Email_off_with_a_back_end_is_an_error()
    {
        var result = Load(SetupFixtures.With("\"email\": { \"enabled\": false }", "\"email\": { \"enabled\": false, \"backend\": \"cloudflare\" }"));

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "email.backend");
    }

    [Fact]
    public void An_unknown_email_key_is_still_refused()
    {
        var result = Load(SetupFixtures.With("\"email\": { \"enabled\": false }", "\"email\": { \"enabled\": false, \"domain\": \"example.invalid\" }"));

        Assert.NotEmpty(result.Issues);
    }
}
