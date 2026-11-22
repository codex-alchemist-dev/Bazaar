using System.Reflection;
using Bazaar.Core;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.ReadAudit;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.ReadAudit.Npgsql;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

public sealed class ReadAuditComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(IConfiguration configuration, NpgsqlDataSource raw)
    {
        var services = new ServiceCollection();
        services.AddSingleton(raw);
        services.AddSingleton<IAuditLog>(new NullAuditLog());
        new ReadAuditComponent().AddServices(services, configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_is_a_feature_after_audit_and_is_in_the_host_list()
    {
        Assert.Equal("read-audit", ReadAuditComponent.ManifestDefinition.Id);
        Assert.Equal(["audit"], ReadAuditComponent.ManifestDefinition.DependsOn);
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "read-audit");

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();
        Assert.True(order.IndexOf("audit") < order.IndexOf("read-audit"));
    }

    [Fact]
    public void Audited_source_is_registered_by_default_over_the_raw_source()
    {
        using var raw = NpgsqlDataSource.Create("Host=localhost;Database=x");
        using var provider = Build(Config(), raw);

        var audited = provider.GetRequiredService<AuditedDbDataSource>();

        Assert.Same(raw, audited.Unaudited);
        Assert.Same(raw, provider.GetRequiredService<NpgsqlDataSource>());
    }

    [Fact]
    public void Operator_can_turn_the_audited_source_off()
    {
        using var raw = NpgsqlDataSource.Create("Host=localhost;Database=x");
        using var provider = Build(Config(("Bazaar:ReadAudit:Enabled", "false")), raw);

        Assert.Null(provider.GetService<AuditedDbDataSource>());
    }

    [Fact]
    public void Untagged_read_options_come_from_configuration()
    {
        using var raw = NpgsqlDataSource.Create("Host=localhost;Database=x");
        using var provider = Build(
            Config(("Bazaar:ReadAudit:RecordUntaggedReads", "false"), ("Bazaar:ReadAudit:UntaggedObjectType", "legacy")), raw);

        var options = provider.GetRequiredService<AuditedDataSourceOptions>();

        Assert.False(options.RecordUntaggedReads);
        Assert.Equal("legacy", options.UntaggedObjectType);
    }

    [Fact]
    public void Audit_log_takes_the_raw_source_and_never_the_audited_one()
    {
        // The audit log's constructor asks for NpgsqlDataSource, and AuditedDbDataSource is not one, so the container cannot
        // hand it the audited source (recursion rule, doc 48 section 5.1).
        Assert.False(typeof(NpgsqlDataSource).IsAssignableFrom(typeof(AuditedDbDataSource)));
        var parameters = typeof(PostgresAuditLog).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single().GetParameters();
        Assert.Equal([typeof(NpgsqlDataSource)], parameters.Select(p => p.ParameterType).ToArray());

        var services = new ServiceCollection();
        using var raw = NpgsqlDataSource.Create("Host=localhost;Database=x");
        services.AddSingleton(raw);
        new AuditComponent().AddServices(services, Config());
        new ReadAuditComponent().AddServices(services, Config());
        var descriptor = services.Last(d => d.ServiceType == typeof(NpgsqlDataSource));
        Assert.Same(raw, descriptor.ImplementationInstance);
        using var provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<PostgresAuditLog>();
        Assert.NotNull(log);
        Assert.Same(log, provider.GetRequiredService<IAuditLog>());
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
