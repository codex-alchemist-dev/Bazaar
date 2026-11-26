using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>The graph sink decorator is order-independent and leaves the Memory module's plain sink alone when it is not used.</summary>
public sealed class MemoryGraphProjectionRegistrationTests
{
    [Fact]
    public void The_Sink_Is_Decorated_Whether_Memory_Registers_Before_Or_After_The_Projection()
    {
        var memoryFirst = new ServiceCollection();
        AddFakeGraphStore(memoryFirst);
        AddMemory(memoryFirst);
        memoryFirst.AddMemoryGraphProjection();

        var projectionFirst = new ServiceCollection();
        AddFakeGraphStore(projectionFirst);
        projectionFirst.AddMemoryGraphProjection();
        AddMemory(projectionFirst);

        using var first = memoryFirst.BuildServiceProvider();
        using var second = projectionFirst.BuildServiceProvider();
        Assert.IsType<MemoryGraphSink>(first.GetRequiredService<IMemoryBatchSink>());
        Assert.IsType<MemoryGraphSink>(second.GetRequiredService<IMemoryBatchSink>());
    }

    [Fact]
    public void Memory_Alone_Registers_The_Plain_Vault_As_The_Sink()
    {
        var services = new ServiceCollection();
        AddMemory(services);

        using var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IMemoryBatchSink>();
        Assert.Same(provider.GetRequiredService<IMemoryVault>(), sink);
    }

    [Fact]
    public void Without_A_Memory_Registration_Resolving_The_Sink_Fails_As_Before()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AddFakeGraphStore(services);
        services.AddMemoryGraphProjection();

        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMemoryBatchSink>());
    }

    private static void AddFakeGraphStore(IServiceCollection services) =>
        services.AddSingleton<IMemoryGraphStore>(new FakeGraphStore());

    private static void AddMemory(IServiceCollection services)
    {
        services.AddLogging();
        var values = new Dictionary<string, string?>
        {
            [MemoryComponent.RepoPathKey] = Path.Combine(Path.GetTempPath(), "bazaar-graphreg-" + Guid.NewGuid().ToString("N")),
            [MemoryComponent.WriterNameKey] = "Test Writer",
            [MemoryComponent.WriterEmailKey] = "writer@example.invalid",
            [MemoryComponent.AuthorEmailFormatKey] = "{agent}@example.invalid",
        };
        new MemoryComponent().AddServices(services, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private sealed class FakeGraphStore : IMemoryGraphStore
    {
        public Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsByDocAsync(Guid docId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);
    }
}
