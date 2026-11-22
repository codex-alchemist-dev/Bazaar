using System.Text.Json;

using Bazaar.Server.Setup;

namespace Bazaar.Client.CompanySetup.Tests;

public class SetupPlanTextTests
{
    private static CompanySetupRequest Request(string kind = "dpapi", string reference = "dpapi:keys/kek.bin") =>
        new("Example Studio", "example-studio", "Dana", "en", "Europe/London", kind, reference, AcknowledgeNoBackupTarget: true);

    /// <summary>
    /// The plan's network block always maps to these two keys, so the loader needs them registered. Nothing else is set.
    /// </summary>
    private sealed class NetworkSettings : ISetupSettingCatalog
    {
        private readonly Dictionary<string, ISetupSettingDefinition> _byName = new(StringComparer.Ordinal)
        {
            ["host.status.bind"] = new Definition("host.status.bind", typeof(string), value => value.ValueKind == JsonValueKind.String),
            ["host.status.port"] = new Definition("host.status.port", typeof(int), value => value.ValueKind == JsonValueKind.Number),
        };

        public IReadOnlyCollection<ISetupSettingDefinition> Definitions => _byName.Values;

        public bool TryGet(string name, out ISetupSettingDefinition? definition) => _byName.TryGetValue(name, out definition);

        private sealed class Definition(string name, Type valueType, Func<JsonElement, bool> shape) : ISetupSettingDefinition
        {
            public string Name => name;

            public bool IsInstallScope => true;

            public Type ValueType => valueType;

            public string? CheckValue(JsonElement value) => shape(value) ? null : "has the wrong type.";

            public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed) =>
                new(true, null, proposed.GetRawText());
        }
    }

    private static SetupLoadResult Load(string json) => SetupPlanLoader.Load(json, new NetworkSettings());

    [Fact]
    public void The_plan_this_client_writes_is_accepted_by_the_servers_own_loader()
    {
        var loaded = Load(SetupPlanText.Build(Request()));

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
        Assert.False(Load(SetupPlanText.Build(Request())).Plan!.FirstRun.AcceptConstitution);
        Assert.True(Load(SetupPlanText.Build(Request() with { AcceptConstitution = true })).Plan!.FirstRun.AcceptConstitution);
    }

    [Fact]
    public void The_first_agent_is_named_Claude_unless_changed_and_carries_its_purpose()
    {
        var plan = Load(SetupPlanText.Build(Request())).Plan!;
        Assert.Equal("Claude", plan.FirstRun.DefaultAgentName);

        var custom = Load(SetupPlanText.Build(Request() with { AgentName = "Sage", AgentPurpose = "Triage the board" })).Plan!;
        Assert.Equal("Sage", custom.FirstRun.DefaultAgentName);
        Assert.Equal("Triage the board", custom.FirstRun.DefaultAgentPurpose);
    }

    [Fact]
    public void The_same_request_always_gives_the_same_plan_hash()
    {
        var first = Load(SetupPlanText.Build(Request())).PlanHash;
        var second = Load(SetupPlanText.Build(Request())).PlanHash;
        var different = Load(SetupPlanText.Build(Request() with { OwnerName = "Sam" })).PlanHash;

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void Missing_backup_acknowledgement_is_held_by_the_server_loader()
    {
        var loaded = Load(SetupPlanText.Build(Request() with { AcknowledgeNoBackupTarget = false }));

        Assert.Contains(loaded.Issues, i => i.Path.StartsWith("backups", StringComparison.Ordinal));
        Assert.Equal(SetupExitCode.Held, loaded.ExitCode);
    }

    [Fact]
    public void A_key_reference_that_does_not_match_its_provider_is_reported_by_the_server_loader()
    {
        var loaded = Load(SetupPlanText.Build(Request(kind: "dpapi", reference: "env:KEK")));

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
        Assert.DoesNotContain(Load(SetupPlanText.Build(Request() with { CompanySlug = slug })).Issues, i => i.Path == "install.companySlug");
    }
}
