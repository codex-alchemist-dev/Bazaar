using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Mentions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Mentions;

/// <summary>What <c>AddGcAgentPostSink</c> registers for mentions. No test here connects to a database.</summary>
public sealed class MentionRegistrationTests
{
    private static ServiceCollection Host()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcLogWriter>(new UnusedLogWriter());
        services.AddSingleton<IGcBodyCodec, PlaintextGcBodyCodec>();
        services.AddSingleton<ILoopGuard>(new LoopGuardFacade(TimeProvider.System));
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost"));
        return services;
    }

    [Fact]
    public void The_sql_directory_and_the_inbox_writer_are_registered()
    {
        var services = Host();
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqlGcMentionDirectory>(provider.GetRequiredService<IGcMentionDirectory>());
        Assert.IsType<InboxRowWriter>(provider.GetRequiredService<IInboxRowWriter>());
        Assert.NotNull(provider.GetRequiredService<PrincipalHandleRepository>());
    }

    [Fact]
    public void The_sink_resolves_with_the_directory_and_the_inbox_writer()
    {
        // The sink's constructor throws when it gets a directory without an inbox writer, so resolving proves both arrive.
        var services = Host();
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<GcAgentPostSink>(provider.GetRequiredService<IAgentPostSink>());
    }

    [Fact]
    public void Registering_twice_adds_each_service_once()
    {
        var services = Host();
        services.AddGcAgentPostSink();
        services.AddGcAgentPostSink();

        Assert.Single(services, d => d.ServiceType == typeof(IGcMentionDirectory));
        Assert.Single(services, d => d.ServiceType == typeof(IInboxRowWriter));
        Assert.Single(services, d => d.ServiceType == typeof(PrincipalHandleRepository));
        Assert.Single(services, d => d.ServiceType == typeof(IAgentPostSink));
    }

    [Fact]
    public void A_directory_and_an_inbox_writer_set_by_the_host_are_kept()
    {
        var directory = new EmptyDirectory();
        var inbox = new DroppingInboxWriter();
        var services = Host();
        services.AddSingleton<IGcMentionDirectory>(directory);
        services.AddSingleton<IInboxRowWriter>(inbox);
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();

        Assert.Same(directory, provider.GetRequiredService<IGcMentionDirectory>());
        Assert.Same(inbox, provider.GetRequiredService<IInboxRowWriter>());
        Assert.IsType<GcAgentPostSink>(provider.GetRequiredService<IAgentPostSink>());
    }

    [Fact]
    public void Without_a_data_source_the_registered_sink_cannot_be_resolved()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcLogWriter>(new UnusedLogWriter());
        services.AddSingleton<IGcBodyCodec, PlaintextGcBodyCodec>();
        services.AddSingleton<ILoopGuard>(new LoopGuardFacade(TimeProvider.System));
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<InvalidOperationException>(() => provider.GetRequiredService<IAgentPostSink>());
    }

    private sealed class UnusedLogWriter : IGcLogWriter
    {
        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Registration tests never post.");

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException("Registration tests never post.");
    }

    private sealed class EmptyDirectory : IGcMentionDirectory
    {
        public Task<GcMentionRoster> LoadAsync(Guid gcId, CancellationToken ct) =>
            Task.FromResult(new GcMentionRoster(Guid.Empty, []));
    }

    private sealed class DroppingInboxWriter : IInboxRowWriter
    {
        public Task<int> WriteAsync(IReadOnlyList<InboxRow> rows, CancellationToken ct) => Task.FromResult(0);

        public Task<int> WriteAsync(NpgsqlTransaction tx, IReadOnlyList<InboxRow> rows, CancellationToken ct) => Task.FromResult(0);
    }
}
