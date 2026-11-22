using Bazaar.Client.Tui.Dashboard;
using Bazaar.Roles.Abstractions;
using Bazaar.Roles.Tests;
using Bazaar.Server.OpsPorts;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Client.Tui.Host.Tests;

/// <summary>The real composition root, run over a data source that never connects and an audit log that records in memory.</summary>
public sealed class ServerWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-tuiwiring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class MemoryAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Variables_become_configuration_in_either_case()
    {
        var configuration = ServerTuiWiring.ConfigurationFrom(new Dictionary<string, string?>
        {
            ["Bazaar__Ops__LogFile"] = "/a/log.jsonl",
            ["BAZAAR__OPS__DATADIRECTORY"] = "/a",
            ["PATH"] = "/usr/bin",
        });

        Assert.Equal("/a/log.jsonl", configuration[OpsPortsOptions.LogFileKey]);
        Assert.Equal("/a", configuration[OpsPortsOptions.DataDirectoryKey]);
        Assert.Null(configuration["PATH"]);
    }

    [Fact]
    public async Task The_graph_builds_and_the_ports_use_the_configured_files()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Port=1;Database=x").Build();
        var configuration = ServerTuiWiring.ConfigurationFrom(new Dictionary<string, string?> { ["BAZAAR__OPS__DATADIRECTORY"] = _dir });
        await using var wiring = ServerTuiWiring.Compose(dataSource, new MemoryAudit(), configuration, TimeProvider.System);

        await wiring.Crashes.RecordAsync(new OpsCrashNote(DateTimeOffset.UtcNow, "crashed with X"));

        Assert.True(File.Exists(Path.Combine(_dir, "tui-crashes.jsonl")));
        Assert.NotNull(wiring.Ports.Logs);
        Assert.NotNull(wiring.Ports.Events);
        Assert.NotNull(wiring.Ports.Audit);
        Assert.NotNull(wiring.Ports.Status);
        Assert.NotNull(wiring.Ports.Kodo);
        // Ports whose owning module has a read model are wired; the ones without stay unavailable (doc 58 4a).
        Assert.NotNull(wiring.Ports.Users);
        Assert.NotNull(wiring.Ports.Devices);
        Assert.NotNull(wiring.Ports.Approvals);
        Assert.Null(wiring.Ports.Modules);
        Assert.NotNull(wiring.Writes.Devices);
        Assert.NotNull(wiring.Writes.Members);
        Assert.NotNull(wiring.Writes.Restorer);
        Assert.Null(wiring.Writes.Approvals);
        Assert.Null(wiring.Writes.StepUp);
        Assert.Null(wiring.Writes.Modules);
    }

    [Fact]
    public async Task The_wired_directories_read_through_the_data_source_so_an_unreachable_database_throws_and_the_screen_shows_unavailable()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Port=1;Database=x;Timeout=1").Build();
        var configuration = ServerTuiWiring.ConfigurationFrom(new Dictionary<string, string?> { ["BAZAAR__OPS__DATADIRECTORY"] = _dir });
        await using var wiring = ServerTuiWiring.Compose(dataSource, new MemoryAudit(), configuration, TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(() => wiring.Ports.Users!.ListAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => wiring.Ports.Approvals!.ListPendingAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_roles_check_in_the_graph_is_the_real_one_so_a_sign_in_without_the_owner_role_is_refused()
    {
        // The data source never connects, so the real Postgres role store throws when asked: the gate must fail closed, not open.
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Port=1;Database=x;Timeout=1").Build();
        var audit = new MemoryAudit();
        await using var wiring = ServerTuiWiring.Compose(
            dataSource, audit, ServerTuiWiring.ConfigurationFrom(new Dictionary<string, string?> { ["BAZAAR__OPS__DATADIRECTORY"] = _dir }), TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(() => wiring.SignIn.SignInOwnerAsync());

        Assert.Empty(audit.Entries);
    }
}
