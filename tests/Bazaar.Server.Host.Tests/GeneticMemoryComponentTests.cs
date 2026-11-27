using Cdax.Core;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

public sealed class GeneticMemoryComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private sealed class NullWriter : IGmtLessonWriter, IGmtSecretScanner
    {
        public Task<GmtWriteResult> WriteAsync(GmtLessonWrite write, string writerAgentId, CancellationToken ct) =>
            Task.FromResult(new GmtWriteResult(GmtWriteStatus.Written));

        public bool ContainsSecret(string text) => false;
    }

    private static ServiceProvider Build(IConfiguration configuration, bool withSink)
    {
        var services = new ServiceCollection();
        new GeneticMemoryComponent().AddServices(services, configuration);
        if (withSink)
        {
            var writer = new NullWriter();
            services.AddSingleton<IGmtLessonWriter>(writer);
            services.AddSingleton<IGmtSecretScanner>(writer);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_names_the_module_with_soft_dependencies_on_memory_and_vault()
    {
        var manifest = GeneticMemoryComponent.ManifestDefinition;

        Assert.Equal("genetic-memory", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["memory", "vault"], manifest.Soft);
    }

    [Fact]
    public void Host_ships_the_component_and_the_graph_accepts_it()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "genetic-memory");

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();

        Assert.Contains("genetic-memory", order);
    }

    [Fact]
    public void The_default_reviewer_is_the_architect_and_the_option_can_route_lessons_to_the_owner()
    {
        using var defaults = Build(Config(), withSink: false);
        using var owner = Build(Config(("Bazaar:GeneticMemory:Reviewer", "owner")), withSink: false);

        Assert.Equal(GmtReviewer.Architect, defaults.GetRequiredService<GmtOptions>().LessonReviewer);
        Assert.Equal(GmtReviewer.Owner, owner.GetRequiredService<GmtOptions>().LessonReviewer);
    }

    [Fact]
    public void A_reviewer_that_does_not_parse_fails_at_start()
    {
        Assert.Throws<InvalidOperationException>(() => Build(Config(("Bazaar:GeneticMemory:Reviewer", "everyone")), withSink: false));
    }

    [Fact]
    public void The_service_resolves_once_a_memory_sink_exists_and_not_before()
    {
        using var without = Build(Config(), withSink: false);
        using var with = Build(Config(), withSink: true);

        Assert.Throws<InvalidOperationException>(() => without.GetRequiredService<IGeneticMemoryTransfer>());
        Assert.IsType<GeneticMemoryService>(with.GetRequiredService<IGeneticMemoryTransfer>());
    }

    [Fact]
    public void Host_migration_source_includes_the_genetic_memory_namespace()
    {
        var source = HostMigrations.Source();

        Assert.Contains("genetic_memory", source.Namespaces());
        Assert.Equal([1], source.Load("genetic_memory").Select(f => f.Version).ToList());
    }

    [Fact]
    public void The_ledger_is_in_memory_without_a_data_source_and_postgres_with_one()
    {
        using var without = Build(Config(), withSink: false);
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        new GeneticMemoryComponent().AddServices(services, Config());
        using var with = services.BuildServiceProvider();

        Assert.IsType<InMemoryGmtLedger>(without.GetRequiredService<IGmtLedger>());
        Assert.IsType<PgGmtLedger>(with.GetRequiredService<IGmtLedger>());
    }

    [Fact]
    public void The_proposal_tool_is_off_by_default()
    {
        using var provider = Build(Config(), withSink: true);

        Assert.Empty(provider.GetServices<ToolRegistration>());
        Assert.Null(provider.GetService<IGmtProposalSource>());
        Assert.False(provider.GetRequiredService<GmtOptions>().ProposalToolEnabled);
    }

    [Fact]
    public void Turning_the_proposal_tool_on_registers_the_tool_its_definition_and_the_source()
    {
        using var provider = Build(Config(("Bazaar:GeneticMemory:ProposalTool:Enabled", "true"), ("Bazaar:GeneticMemory:ProposalTool:MaxPerChild", "3")), withSink: true);

        var registration = Assert.Single(provider.GetServices<ToolRegistration>());
        Assert.Equal(GmtToolNames.ProposeLesson, registration.Name);
        Assert.IsType<GmtProposeLessonTool>(registration.Tool);
        Assert.Contains(provider.GetServices<Bazaar.Contracts.Gateway.ModelToolDefinition>(), d => d.Name == GmtToolNames.ProposeLesson);
        Assert.IsType<GmtRecordedProposalSource>(provider.GetRequiredService<IGmtProposalSource>());
        Assert.Equal(3, provider.GetRequiredService<GmtOptions>().MaxProposalsPerChild);
    }

    [Theory]
    [InlineData("Bazaar:GeneticMemory:ProposalTool:Enabled", "maybe")]
    [InlineData("Bazaar:GeneticMemory:ProposalTool:MaxPerChild", "0")]
    [InlineData("Bazaar:GeneticMemory:ProposalTool:MaxPerChild", "51")]
    public void A_bad_proposal_tool_option_fails_at_start(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(Config((key, value)), withSink: false));
    }

    [Fact]
    public void The_end_hook_is_registered_and_does_nothing_without_a_proposal_source()
    {
        using var provider = Build(Config(), withSink: true);

        var hook = Assert.Single(provider.GetServices<ISubagentEndHook>());
        Assert.NotNull(hook);
    }
}
