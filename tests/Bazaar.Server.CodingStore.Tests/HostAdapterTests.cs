using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingStore;
using Bazaar.Server.CodingStore.Data;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.CodingStore.Tests;

/// <summary>The host adapters and wiring. These need no database.</summary>
public sealed class HostAdapterTests
{
    private sealed class FakeLog : IToolCallLog
    {
        public List<ToolCallRecord> Rows { get; } = [];

        public ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken)
        {
            Rows.Add(record);
            return ValueTask.CompletedTask;
        }

        public List<(Guid Id, ToolCallStatus Status, int? Exit)> Completions { get; } = [];

        public ValueTask<bool> CompleteAsync(Guid id, ToolCallStatus status, int? exitCode, CancellationToken cancellationToken)
        {
            Completions.Add((id, status, exitCode));
            return ValueTask.FromResult(true);
        }
    }

    [Fact]
    public async Task Tool_call_trace_forwards_completion_to_the_log()
    {
        var log = new FakeLog();
        var id = Guid.NewGuid();

        var ok = await new ToolCallLogTrace(log).CompleteAsync(id, ToolCallStatus.Failed, 2, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal((id, ToolCallStatus.Failed, (int?)2), Assert.Single(log.Completions));
    }

    private static SubagentTraceEntry Entry(SubagentTraceKind kind, SubagentStatus? status = null) => new(
        kind, "sub-1", "parent-1", "chain-1", 1, "haiku", ["read", "edit"], default, ["src/a.cs"],
        new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), status);

    [Fact]
    public async Task Tool_call_trace_forwards_the_row_unchanged()
    {
        var log = new FakeLog();
        var row = new ToolCallRecord(Guid.NewGuid(), "s", "t", "Exec", "agent", false, ToolCallStatus.Unknown, null, "h", DateTimeOffset.UnixEpoch, null);

        await new ToolCallLogTrace(log).AppendAsync(row, TestContext.Current.CancellationToken);

        Assert.Same(row, Assert.Single(log.Rows));
    }

    [Fact]
    public async Task Subagent_spawn_becomes_one_row_under_the_parent_session()
    {
        var log = new FakeLog();

        await new ToolCallLogSubagentTrace(log).AppendAsync(Entry(SubagentTraceKind.Spawned), TestContext.Current.CancellationToken);

        var row = Assert.Single(log.Rows);
        Assert.Equal("parent-1", row.SessionId);
        Assert.Equal(ToolCallLogSubagentTrace.SpawnTool, row.Tool);
        Assert.Equal(ToolCallStatus.Succeeded, row.Status);
        Assert.Equal(64, row.ArgsSha256.Length);
    }

    [Theory]
    [InlineData(SubagentStatus.Completed, ToolCallStatus.Succeeded)]
    [InlineData(SubagentStatus.Failed, ToolCallStatus.Failed)]
    [InlineData(SubagentStatus.Cancelled, ToolCallStatus.Unknown)]
    public async Task Subagent_finish_maps_the_run_status(SubagentStatus status, ToolCallStatus expected)
    {
        var log = new FakeLog();

        await new ToolCallLogSubagentTrace(log).AppendAsync(Entry(SubagentTraceKind.Finished, status), TestContext.Current.CancellationToken);

        var row = Assert.Single(log.Rows);
        Assert.Equal(ToolCallLogSubagentTrace.FinishTool, row.Tool);
        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public async Task Subagent_args_hash_differs_between_entries()
    {
        var log = new FakeLog();
        var trace = new ToolCallLogSubagentTrace(log);
        await trace.AppendAsync(Entry(SubagentTraceKind.Spawned), TestContext.Current.CancellationToken);
        await trace.AppendAsync(Entry(SubagentTraceKind.Spawned) with { SubagentId = "sub-2" }, TestContext.Current.CancellationToken);

        Assert.NotEqual(log.Rows[0].ArgsSha256, log.Rows[1].ArgsSha256);
    }

    [Fact]
    public void Harness_uses_in_process_traces_without_a_data_source()
    {
        using var sp = new ServiceCollection().AddBazaarCodingHarness(EnabledConfig()).BuildServiceProvider();

        Assert.IsType<InProcessToolCallTrace>(sp.GetRequiredService<IToolCallTrace>());
        Assert.IsType<InProcessSubagentTrace>(sp.GetRequiredService<ISubagentTrace>());
    }

    [Fact]
    public async Task Harness_uses_durable_traces_when_a_data_source_is_registered()
    {
        await using var ds = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=x");
        var services = new ServiceCollection();
        services.AddBazaarCodingHarness(EnabledConfig());
        services.AddSingleton(ds);
        await using var sp = services.BuildServiceProvider();

        Assert.IsType<ToolCallLogTrace>(sp.GetRequiredService<IToolCallTrace>());
        Assert.IsType<ToolCallLogSubagentTrace>(sp.GetRequiredService<ISubagentTrace>());
        Assert.IsType<PostgresToolCallStore>(sp.GetRequiredService<IToolCallLog>());
    }

    [Fact]
    public void Host_migration_source_carries_the_coding_namespace_in_order()
    {
        var files = HostMigrations.Source().Load("coding");

        Assert.Equal([1, 2, 3], files.Select(f => f.Version).Order());
    }

    private static IConfiguration EnabledConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coding:Harness:Enabled"] = "true",
                ["Coding:Harness:Git:AppNoreplyEmail"] = "bot@noreply.example.test",
            })
            .Build();
}
