using Bazaar.Client.Tui.Dashboard;
using Bazaar.Roles.Abstractions;
using Bazaar.Roles.Tests;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.OpsPorts.Tests;

public class ComponentAndAuditTests
{
    [Fact]
    public void Summary_shows_flat_fields_hides_secret_looking_ones_and_skips_nested_values()
    {
        var summary = PostgresAdminAuditReader.Summarize(
            """{"principal":"p1","area":"Kodo","sessionToken":"abc","code":"123456","nested":{"x":1},"list":[1],"count":3,"ok":true}""");

        Assert.Equal("principal=p1 area=Kodo sessionToken=*** code=*** count=3 ok=True", summary);
        Assert.DoesNotContain("abc", summary);
        Assert.DoesNotContain("123456", summary);
    }

    [Fact]
    public void Summary_strips_control_characters_and_is_bounded()
    {
        var escape = PostgresAdminAuditReader.Summarize("{\"name\":\"a\\u001b[31mred\"}");
        var long1 = PostgresAdminAuditReader.Summarize("{\"a\":\"" + new string('x', 400) + "\"}");

        Assert.DoesNotContain('\u001b', escape);
        Assert.True(long1.Length <= PostgresAdminAuditReader.MaxSummaryLength);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void Summary_of_a_payload_that_is_not_an_object_is_empty(string payload) =>
        Assert.Equal(string.Empty, PostgresAdminAuditReader.Summarize(payload));

    [Fact]
    public void Options_default_into_one_data_directory_and_every_value_can_be_set()
    {
        var defaults = OpsPortsOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(Path.Combine(defaults.DataDirectory, "server-log.jsonl"), defaults.LogFile);
        Assert.Equal(Path.Combine(defaults.DataDirectory, "health-events.jsonl"), defaults.EventFile);
        Assert.Equal(Path.Combine(defaults.DataDirectory, "tui-crashes.jsonl"), defaults.CrashJournalFile);
        Assert.Null(defaults.OwnerPrincipalId);
        Assert.Equal(OpsPortsOptions.DefaultMaxReadBytes, defaults.MaxReadBytes);

        var owner = Guid.NewGuid();
        var set = OpsPortsOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OpsPortsOptions.DataDirectoryKey] = "/var/bazaar/ops",
            [OpsPortsOptions.LogFileKey] = "/logs/x.jsonl",
            [OpsPortsOptions.OwnerPrincipalKey] = owner.ToString(),
            [OpsPortsOptions.MaxReadBytesKey] = "1",
        }).Build());
        Assert.Equal("/logs/x.jsonl", set.LogFile);
        Assert.Equal(Path.Combine("/var/bazaar/ops", "health-events.jsonl"), set.EventFile);
        Assert.Equal(owner, set.OwnerPrincipalId);
        Assert.Equal(4 * 1024, set.MaxReadBytes);
    }

    [Fact]
    public void Blank_and_malformed_settings_fall_back_to_the_defaults()
    {
        var options = OpsPortsOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OpsPortsOptions.LogFileKey] = "   ",
            [OpsPortsOptions.OwnerPrincipalKey] = "not a guid",
            [OpsPortsOptions.MaxReadBytesKey] = "-5",
        }).Build());

        Assert.EndsWith("server-log.jsonl", options.LogFile, StringComparison.Ordinal);
        Assert.Null(options.OwnerPrincipalId);
        Assert.Equal(OpsPortsOptions.DefaultMaxReadBytes, options.MaxReadBytes);
    }

    [Fact]
    public void The_component_registers_every_port_it_owns_and_an_existing_registration_wins()
    {
        var services = new ServiceCollection();
        var mine = new FixedLogs();
        services.AddSingleton<IOpsLogSource>(mine);
        services.AddSingleton(new NpgsqlDataSourceBuilder("Host=localhost;Port=1;Database=x").Build());
        services.AddSingleton<IRoleStore>(new InMemoryRoleStore());
        services.AddSingleton<IPermissionChecker>(new NeverChecker());

        new OpsPortsComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IOpsLogSource>());
        Assert.IsType<FileOpsEventSource>(provider.GetRequiredService<IOpsEventSource>());
        Assert.IsType<FileOpsCrashJournal>(provider.GetRequiredService<IOpsCrashJournal>());
        Assert.IsType<RolesOpsAuthorizer>(provider.GetRequiredService<IOpsAuthorizer>());
        Assert.IsType<RoleStoreOwnerLocalGate>(provider.GetRequiredService<IOwnerLocalGate>());
        Assert.IsType<PostgresAdminAuditReader>(provider.GetRequiredService<IAdminAuditReader>());
        Assert.NotNull(provider.GetRequiredService<OpsLogFileWriter>());
        Assert.NotNull(provider.GetRequiredService<OpsEventFileWriter>());
    }

    [Fact]
    public void The_manifest_names_the_module_and_what_it_needs()
    {
        var manifest = new OpsPortsComponent().Manifest;

        Assert.Equal("ops-ports", manifest.Id);
        Assert.Contains("feature:ops-ports", manifest.Provides);
        Assert.Contains("roles", manifest.DependsOn);
    }

    /// <summary>Doc 58 and the module rules: the module reads the server only through abstractions and the web admin, never Bazaar.Server.</summary>
    [Fact]
    public void The_assembly_never_references_bazaar_server_or_client_core()
    {
        var names = typeof(OpsPortsComponent).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        Assert.DoesNotContain("Bazaar.Server", names);
        Assert.DoesNotContain("Bazaar.Client.Core", names);
    }

    private sealed class FixedLogs : IOpsLogSource
    {
        public Task<IReadOnlyList<OpsLogLine>> TailAsync(OpsLogFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpsLogLine>>([]);
    }

    private sealed class NeverChecker : IPermissionChecker
    {
        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
