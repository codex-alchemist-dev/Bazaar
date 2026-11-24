using Bazaar.Core;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Hive.Settings;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

public sealed class HiveComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(IConfiguration configuration, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        new HiveComponent().AddServices(services, configuration);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class NullVault : IMemoryBatchSink, IHiveNoteReader
    {
        public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct) =>
            Task.FromResult(MemoryCommitResult.Success("sha"));

        public Task<string?> ReadAsync(string path, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void Manifest_names_hive_with_soft_dependencies_on_memory_and_vault()
    {
        var manifest = HiveComponent.ManifestDefinition;

        Assert.Equal("hive", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["memory", "vault"], manifest.Soft);
        Assert.Equal(HiveComponent.MigrationProvider, manifest.MigrationNamespace);
    }

    [Fact]
    public void Host_ships_the_hive_component_and_the_graph_accepts_its_soft_dependencies()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "hive");

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();

        // Soft edges never fail the graph (BAZAAR-COMPONENTS.md section 4.1), whether or not "memory" is shipped.
        Assert.Contains("hive", order);
    }

    [Fact]
    public void Host_migration_source_includes_the_hive_namespace()
    {
        var source = HostMigrations.Source();

        Assert.Contains("hive", source.Namespaces());
        Assert.Equal([1, 2], source.Load("hive").Select(f => f.Version).ToList());
    }

    [Fact]
    public void Without_a_data_source_the_in_memory_stores_are_used()
    {
        using var provider = Build(Config());

        Assert.IsType<InMemoryHiveGrantStore>(provider.GetRequiredService<IHiveGrantStore>());
        Assert.IsType<InMemoryHiveCopyLedger>(provider.GetRequiredService<IHiveCopyLedger>());
        Assert.Same(provider.GetRequiredService<IHiveGrantStore>(), provider.GetRequiredService<IHiveGrantStore>());
        Assert.IsType<HiveGrantPolicy>(provider.GetRequiredService<IHiveGrantPolicy>());
    }

    [Fact]
    public void With_a_data_source_the_postgres_stores_are_used()
    {
        // Creating a data source opens no connection, so a placeholder connection string is enough to check the wiring.
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        using var provider = Build(Config(), s => s.AddSingleton(dataSource));

        Assert.IsType<PgHiveGrantStore>(provider.GetRequiredService<IHiveGrantStore>());
        Assert.IsType<PgHiveLedger>(provider.GetRequiredService<IHiveCopyLedger>());
    }

    [Fact]
    public void A_store_registered_earlier_is_kept()
    {
        var custom = new InMemoryHiveGrantStore();
        using var provider = Build(Config(), s => s.AddSingleton<IHiveGrantStore>(custom));

        // The component registered after it, so TryAdd leaves the custom store in place.
        Assert.Same(custom, provider.GetRequiredService<IHiveGrantStore>());
    }

    [Fact]
    public void Copy_service_resolves_once_memory_and_vault_services_exist()
    {
        var vault = new NullVault();
        using var provider = Build(Config(), s =>
        {
            s.AddSingleton<IMemoryBatchSink>(vault);
            s.AddSingleton<IHiveNoteReader>(vault);
        });

        Assert.NotNull(provider.GetRequiredService<HiveCopyService>());
    }

    [Fact]
    public void Copy_service_fails_with_a_clear_error_while_memory_and_vault_are_absent()
    {
        using var provider = Build(Config());

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<HiveCopyService>());

        Assert.Contains("IHiveNoteReader", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_default_to_the_protective_values()
    {
        using var provider = Build(Config());
        var options = provider.GetRequiredService<HiveCopyOptions>();

        Assert.Equal(30, options.ReviewDays);
        Assert.Equal(new HashSet<HiveKind> { HiveKind.Procedure, HiveKind.Failure }, options.AllowedKinds);
        Assert.Equal(MemoryTier.AgentVerified, options.MaxCopyTier);
    }

    [Fact]
    public void Options_bind_from_the_hive_section()
    {
        using var provider = Build(Config(
            ("Bazaar:Hive:ReviewDays", "0"),
            ("Bazaar:Hive:AllowedKinds:0", "failure"),
            ("Bazaar:Hive:AllowedKinds:1", "technical-fact"),
            ("Bazaar:Hive:WriterAgentId", "hive-writer"),
            ("Bazaar:Hive:MaxCopyTier", "UntrustedDerived")));
        var options = provider.GetRequiredService<HiveCopyOptions>();

        Assert.Equal(0, options.ReviewDays);
        Assert.Equal(new HashSet<HiveKind> { HiveKind.Failure, HiveKind.TechnicalFact }, options.AllowedKinds);
        Assert.Equal("hive-writer", options.WriterAgentId);
        Assert.Equal(MemoryTier.UntrustedDerived, options.MaxCopyTier);
    }

    [Theory]
    [InlineData("Bazaar:Hive:ReviewDays", "366")]
    [InlineData("Bazaar:Hive:ReviewDays", "-1")]
    [InlineData("Bazaar:Hive:ReviewDays", "soon")]
    [InlineData("Bazaar:Hive:AllowedKinds:0", "preference")]
    [InlineData("Bazaar:Hive:MaxCopyTier", "OwnerPromoted")]
    [InlineData("Bazaar:Hive:MaxCopyTier", "gold")]
    public void Options_that_would_loosen_a_limit_or_do_not_parse_fail_at_start(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => new HiveComponent().AddServices(new ServiceCollection(), Config((key, value))));
    }

    [Fact]
    public void Host_components_resolve_the_copy_service_with_the_vault_backed_reader()
    {
        var repo = Path.Combine(Path.GetTempPath(), "bazaar-hive-host-" + Guid.NewGuid().ToString("N"));
        var configuration = Config(
            (MemoryComponent.RepoPathKey, repo),
            (MemoryComponent.WorktreeRootKey, repo + ".worktrees"),
            (MemoryComponent.WriterNameKey, "Test Writer"),
            (MemoryComponent.WriterEmailKey, "writer@example.invalid"),
            (MemoryComponent.AuthorEmailFormatKey, "{agent}@example.invalid"));
        var services = new ServiceCollection();
        services.AddLogging();
        // The same component instances the host ships, in host order.
        foreach (var component in HostComponents.All.Where(c => c is MemoryComponent or HiveComponent))
        {
            component.AddServices(services, configuration);
        }

        using var provider = services.BuildServiceProvider();

        Assert.IsType<MemoryVaultHiveNoteReader>(provider.GetRequiredService<IHiveNoteReader>());
        Assert.NotNull(provider.GetRequiredService<HiveCopyService>());
    }

    [Fact]
    public void Hive_registers_its_two_setting_keys_with_the_registry_contributions()
    {
        using var provider = Build(Config());

        var names = provider.GetServices<ISettingContribution>().SelectMany(c => c.Settings).Select(s => s.Name).ToList();

        Assert.Equal(["hive.grants", "hive.allowed_kinds"], names);
    }

    [Fact]
    public void With_a_settings_store_the_grants_and_kinds_come_from_settings()
    {
        using var provider = Build(Config(), s => s.AddSingleton<ISettingStore>(new NoSettings()));

        Assert.IsType<SettingsHiveGrantStore>(provider.GetRequiredService<IHiveGrantStore>());
        Assert.IsType<SettingsHiveKindSource>(provider.GetRequiredService<IHiveKindSource>());
    }

    private sealed class NoSettings : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, System.Text.Json.JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
