using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Security.Tests.Audit.Anchors;

/// <summary>AddAuditAnchoring: off by default registers nothing; enabled without a valid target refuses to start.</summary>
public sealed class AuditAnchorRegistrationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Anchoring_is_off_by_default_and_registers_no_anchor_service_store_or_target()
    {
        var services = new ServiceCollection();

        services.AddAuditAnchoring(Config());

        Assert.Empty(services);
    }

    [Fact]
    public void An_explicitly_disabled_section_registers_nothing_even_with_a_target_configured()
    {
        var services = new ServiceCollection();

        services.AddAuditAnchoring(Config(
            ("AuditAnchor:Enabled", "false"),
            ("AuditAnchor:Rfc3161:Endpoint", "https://tsa.example.invalid/tsr")));

        Assert.Empty(services);
    }

    [Fact]
    public void Enabled_anchoring_with_an_unconfigured_target_is_refused_at_startup()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAuditAnchoring(Config(("AuditAnchor:Enabled", "true"))));
    }

    [Fact]
    public void Enabled_anchoring_with_every_target_disabled_is_refused_at_startup()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddAuditAnchoring(Config(
            ("AuditAnchor:Enabled", "true"),
            ("AuditAnchor:Rfc3161:Enabled", "false"),
            ("AuditAnchor:OffsiteGit:Enabled", "false"))));
    }

    [Fact]
    public void Enabled_anchoring_with_a_configured_target_resolves_the_service_without_touching_the_database()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=db.example.invalid;Database=bazaar"));
        services.AddSingleton<IAuditLog, NullAuditLog>();

        services.AddAuditAnchoring(Config(
            ("AuditAnchor:Enabled", "true"),
            ("AuditAnchor:Rfc3161:Endpoint", "https://tsa.example.invalid/tsr"),
            ("AuditAnchor:OffsiteGit:Enabled", "false")));

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<AuditAnchorService>();

        Assert.NotNull(service);
        Assert.IsType<PostgresAuditAnchorStore>(provider.GetRequiredService<IAuditAnchorStore>());
    }

    [Fact]
    public void Enabled_anchoring_over_an_audit_log_that_cannot_join_a_transaction_is_refused_on_resolve()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=db.example.invalid;Database=bazaar"));
        services.AddSingleton<IAuditLog, NonTransactionalAuditLog>();
        services.AddAuditAnchoring(Config(
            ("AuditAnchor:Enabled", "true"),
            ("AuditAnchor:Rfc3161:Endpoint", "https://tsa.example.invalid/tsr"),
            ("AuditAnchor:OffsiteGit:Enabled", "false")));

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<AuditAnchorService>());
    }

    /// <summary>Accepts entries without storing them. Registration does not write, so nothing reaches it here.</summary>
    private sealed class NullAuditLog : ITransactionalAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;

        public Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NonTransactionalAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
