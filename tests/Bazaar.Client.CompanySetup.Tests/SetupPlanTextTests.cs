using Bazaar.Server.Setup;

namespace Bazaar.Client.CompanySetup.Tests;

public class SetupPlanTextTests
{
    private static CompanySetupRequest Request(string kind = "dpapi", string reference = "dpapi:keys/kek.bin") =>
        new("Example Studio", "example-studio", "Dana", "en", "Europe/London", kind, reference, AcknowledgeNoBackupTarget: true);

    [Fact]
    public void The_plan_this_client_writes_is_accepted_by_the_servers_own_loader()
    {
        var loaded = SetupPlanLoader.Load(SetupPlanText.Build(Request()), NoSettings.Instance);

        Assert.DoesNotContain(loaded.Issues, i => i.Severity == SetupIssueSeverity.Error);
        Assert.NotNull(loaded.Plan);
        Assert.Equal(64, loaded.PlanHash!.Length);
        Assert.Equal("Example Studio", loaded.Plan.Install.CompanyName);
        Assert.Equal("example-studio", loaded.Plan.Install.CompanySlug);
        Assert.Equal("Dana", loaded.Plan.Owner.DisplayName);
        Assert.Equal("blank", loaded.Plan.Company.Source);
        Assert.Equal("dpapi", loaded.Plan.Custody.KekProvider);
    }

    [Fact]
    public void Constitution_acceptance_is_off_unless_the_owner_ticks_it()
    {
        Assert.False(SetupPlanLoader.Load(SetupPlanText.Build(Request()), NoSettings.Instance).Plan!.FirstRun.AcceptConstitution);
        Assert.True(SetupPlanLoader.Load(SetupPlanText.Build(Request() with { AcceptConstitution = true }), NoSettings.Instance).Plan!.FirstRun.AcceptConstitution);
    }

    [Fact]
    public void The_first_agent_is_named_Claude_unless_changed_and_carries_its_purpose()
    {
        var plan = SetupPlanLoader.Load(SetupPlanText.Build(Request()), NoSettings.Instance).Plan!;
        Assert.Equal("Claude", plan.FirstRun.DefaultAgentName);

        var custom = SetupPlanLoader.Load(SetupPlanText.Build(Request() with { AgentName = "Sage", AgentPurpose = "Triage the board" }), NoSettings.Instance).Plan!;
        Assert.Equal("Sage", custom.FirstRun.DefaultAgentName);
        Assert.Equal("Triage the board", custom.FirstRun.DefaultAgentPurpose);
    }

    [Fact]
    public void The_same_request_always_gives_the_same_plan_hash()
    {
        var first = SetupPlanLoader.Load(SetupPlanText.Build(Request()), NoSettings.Instance).PlanHash;
        var second = SetupPlanLoader.Load(SetupPlanText.Build(Request()), NoSettings.Instance).PlanHash;
        var different = SetupPlanLoader.Load(SetupPlanText.Build(Request() with { OwnerName = "Sam" }), NoSettings.Instance).PlanHash;

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void Missing_backup_acknowledgement_is_held_by_the_server_loader()
    {
        var loaded = SetupPlanLoader.Load(SetupPlanText.Build(Request() with { AcknowledgeNoBackupTarget = false }), NoSettings.Instance);

        Assert.Contains(loaded.Issues, i => i.Path.StartsWith("backups", StringComparison.Ordinal));
        Assert.Equal(SetupExitCode.Held, loaded.ExitCode);
    }

    [Fact]
    public void A_key_reference_that_does_not_match_its_provider_is_reported_by_the_server_loader()
    {
        var loaded = SetupPlanLoader.Load(SetupPlanText.Build(Request(kind: "dpapi", reference: "env:KEK")), NoSettings.Instance);

        Assert.Contains(loaded.Issues, i => i.Path == "custody.kekRef");
    }

    [Fact]
    public void Quotes_and_control_characters_in_a_name_stay_inside_the_json_string()
    {
        var text = SetupPlanText.Build(Request() with { CompanyName = "A \"B\"\n\\C" });

        using var document = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal("A \"B\"\n\\C", document.RootElement.GetProperty("install").GetProperty("companyName").GetString());
    }

    [Theory]
    [InlineData("Example Studio", "example-studio")]
    [InlineData("  Codex  Alchemist!! ", "codex-alchemist")]
    [InlineData("---", "")]
    [InlineData(null, "")]
    [InlineData("Ünï Co", "n-co")]
    public void Slug_comes_from_the_name(string? name, string expected) => Assert.Equal(expected, SetupPlanText.SlugFrom(name));

    [Fact]
    public void A_derived_slug_is_never_longer_than_the_servers_limit()
    {
        var slug = SetupPlanText.SlugFrom(new string('a', 200));

        Assert.True(slug.Length <= 63);
        Assert.DoesNotContain(SetupPlanLoader.Load(SetupPlanText.Build(Request() with { CompanySlug = slug }), NoSettings.Instance).Issues, i => i.Path == "install.companySlug");
    }
}
