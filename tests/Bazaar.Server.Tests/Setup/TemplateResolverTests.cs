using System.Text.Json;
using Bazaar.Admin.Composition;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The company source: exact lookup through the catalog, the overlay into the plan, and the refusals.</summary>
public sealed class TemplateResolverTests
{
    private static SetupPlan ValidPlan() => SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

    /// <summary>A plan whose only meaningful field for the resolver is its company source. Used for refusals, which never reach the overlay.</summary>
    private static SetupPlan PlanWithSource(string source) => new() { Company = new SetupCompanySection { Source = source } };

    private static InMemoryTemplateCatalog StarterCatalog() =>
        new InMemoryTemplateCatalog().Add("starter", "1.0.0", TemplateFixtures.Starter());

    private static bool HasError(TemplateResolution result, string path) =>
        result.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == path);

    [Fact]
    public async Task An_exact_version_resolves_to_the_loaded_template_and_its_hash()
    {
        var plan = ValidPlan();

        var result = await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Equal("starter", result.TemplateId);
        Assert.Equal("1.0.0", result.TemplateVersion);
        Assert.NotNull(result.Template);
        Assert.Equal((await TemplateFixtures.Load(TemplateFixtures.Starter())).TemplateHash, result.TemplateHash);
        Assert.Equal(3, result.Seeds.Count);
    }

    [Fact]
    public async Task The_overlay_merges_template_modules_and_adds_template_settings_while_the_plan_wins_on_conflict()
    {
        var plan = ValidPlan();

        var result = await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        // The fixture plan names Constellation, GC and KODO; the template adds Calendar. The module set is the union, sorted.
        Assert.Equal(["Bazaar.Calendar", "Bazaar.Constellation", "Bazaar.GC", "Bazaar.KODO"], plan.Modules);
        // The template says 30 and the plan says 20. The plan wins.
        Assert.Equal(20, plan.Settings["host.shutdown_timeout_seconds"].GetInt32());
        // A setting the plan does not set comes from the template.
        Assert.Equal(7, plan.Settings["host.log.retention_days"].GetInt32());
    }

    [Fact]
    public async Task The_merged_plan_still_passes_plan_validation()
    {
        var plan = ValidPlan();
        await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        var issues = new List<SetupValidationIssue>();
        SetupPlanValidator.Validate(plan, RegistrySetupSettingCatalog.Known, issues);

        Assert.DoesNotContain(issues, issue => issue.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public async Task The_merge_changes_the_plan_hash_so_the_hash_must_be_taken_after_the_overlay()
    {
        var plan = ValidPlan();
        var before = SetupPlanCanonicalizer.Hash(plan);

        await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.NotEqual(before, SetupPlanCanonicalizer.Hash(plan));
    }

    [Fact]
    public async Task A_version_that_the_catalog_does_not_hold_fails_clearly_and_changes_nothing()
    {
        var plan = PlanWithSource("template:starter@1.0.1");
        plan.Modules.Add("Bazaar.GC");

        var result = await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Null(result.Template);
        Assert.Null(result.TemplateHash);
        Assert.True(HasError(result, "company.source"));
        Assert.Contains("exact version", result.Issues.Single().Message, StringComparison.Ordinal);
        Assert.Equal(["Bazaar.GC"], plan.Modules);
    }

    [Theory]
    [InlineData("template:starter@^1.0.0")]
    [InlineData("template:starter@~1.0")]
    [InlineData("template:starter@1.x")]
    [InlineData("template:starter@>=1.0.0")]
    [InlineData("template:starter@*")]
    public async Task Version_ranges_are_refused(string source)
    {
        var result = await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(PlanWithSource(source), TestContext.Current.CancellationToken);

        Assert.Null(result.Template);
        Assert.True(HasError(result, "company.source"));
        Assert.Contains("Version ranges are not supported", result.Issues.Single().Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("template:starter")]
    [InlineData("template:Starter@1.0.0")]
    [InlineData("template:@1.0.0")]
    [InlineData("template:starter@")]
    public async Task Malformed_template_sources_are_refused(string source)
    {
        var result = await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(PlanWithSource(source), TestContext.Current.CancellationToken);

        Assert.Null(result.Template);
        Assert.True(HasError(result, "company.source"));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("import:dump-2026-11")]
    public async Task A_blank_or_import_source_names_no_template_and_changes_nothing(string source)
    {
        var plan = PlanWithSource(source);

        var result = await new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        Assert.Null(result.TemplateId);
        Assert.Null(result.Template);
        Assert.Null(result.TemplateHash);
        Assert.Empty(result.Issues);
        Assert.Empty(plan.Modules);
        Assert.Empty(plan.Settings);
    }

    [Fact]
    public async Task A_catalog_that_returns_a_different_template_is_refused()
    {
        var other = TemplateFixtures.Source(TemplateFixtures.With("\"id\": \"starter\"", "\"id\": \"other\""));
        var catalog = new InMemoryTemplateCatalog().Add("starter", "1.0.0", other);
        var plan = ValidPlan();
        var before = plan.Settings.Count;

        var result = await new TemplateResolver(catalog, RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Null(result.Template);
        Assert.True(HasError(result, "company.source"));
        Assert.Contains("not the one it was asked for", result.Issues.Single().Message, StringComparison.Ordinal);
        Assert.Equal(before, plan.Settings.Count);
    }

    [Fact]
    public async Task A_template_with_an_error_is_not_used_and_the_plan_is_left_as_it_was()
    {
        var broken = TemplateFixtures.Source(TemplateFixtures.With("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"surprise\": true,"));
        var catalog = new InMemoryTemplateCatalog().Add("starter", "1.0.0", broken);
        var plan = ValidPlan();
        var moduleCount = plan.Modules.Count;
        var settingCount = plan.Settings.Count;

        var result = await new TemplateResolver(catalog, RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Null(result.Template);
        Assert.Null(result.TemplateHash);
        Assert.Equal(moduleCount, plan.Modules.Count);
        Assert.Equal(settingCount, plan.Settings.Count);
    }

    [Fact]
    public async Task A_template_whose_setting_is_held_still_merges_and_the_result_is_held()
    {
        var held = TemplateFixtures.Source(TemplateFixtures.With("\"host.log.retention_days\": 7", "\"host.service_account\": \"bazaar-alt\""));
        var catalog = new InMemoryTemplateCatalog().Add("starter", "1.0.0", held);
        var plan = ValidPlan();

        var result = await new TemplateResolver(catalog, RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.NotNull(result.Template);
        Assert.True(plan.Settings.ContainsKey("host.service_account"));
    }

    [Fact]
    public async Task Resolving_the_same_plan_twice_merges_once()
    {
        var plan = ValidPlan();
        var resolver = new TemplateResolver(StarterCatalog(), RegistrySetupSettingCatalog.Known);

        await resolver.ResolveAsync(plan, TestContext.Current.CancellationToken);
        var afterFirst = SetupPlanCanonicalizer.Hash(plan);
        await resolver.ResolveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(afterFirst, SetupPlanCanonicalizer.Hash(plan));
    }

    [Fact]
    public async Task The_overlay_copies_values_so_a_later_change_to_the_template_cannot_reach_the_plan()
    {
        var template = (await TemplateFixtures.Load(TemplateFixtures.Starter())).Template!;
        var plan = ValidPlan();

        TemplateOverlay.Apply(plan, template);
        template.Settings["host.log.retention_days"] = JsonSerializer.SerializeToElement(99);

        Assert.Equal(7, plan.Settings["host.log.retention_days"].GetInt32());
    }
}
