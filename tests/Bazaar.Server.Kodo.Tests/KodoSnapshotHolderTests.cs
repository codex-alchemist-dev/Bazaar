using Bazaar.Contracts.Kodo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Kodo.Tests;

public class KodoSnapshotHolderTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static KodoStatusSnapshotDto Snap(long revision, int score = 90, int asOfSeconds = 0) =>
        new(KodoStatusSnapshotDto.CurrentSchemaVersion, revision,
            new KodoStatusDto("healthy", null, score, _t0.AddSeconds(asOfSeconds), []), null, []);

    [Fact]
    public void Empty_until_published()
    {
        Assert.Null(new KodoSnapshotHolder().Current);
    }

    [Fact]
    public void Newer_revision_replaces_and_older_is_dropped()
    {
        var holder = new KodoSnapshotHolder();
        Assert.True(holder.Publish(Snap(5)));
        Assert.True(holder.Publish(Snap(6)));
        Assert.False(holder.Publish(Snap(4)));
        Assert.Equal(6, holder.Current!.Revision);
    }

    [Fact]
    public void Equal_revision_needs_a_later_as_of()
    {
        var holder = new KodoSnapshotHolder();
        Assert.True(holder.Publish(Snap(3, score: 80, asOfSeconds: 10)));
        Assert.False(holder.Publish(Snap(3, score: 50, asOfSeconds: 10)));
        Assert.False(holder.Publish(Snap(3, score: 50, asOfSeconds: 5)));
        Assert.True(holder.Publish(Snap(3, score: 70, asOfSeconds: 11)));
        Assert.Equal(70, holder.Current!.Status.Score);
    }

    [Fact]
    public async Task Concurrent_publishers_end_on_the_highest_revision()
    {
        var holder = new KodoSnapshotHolder();
        const int writers = 8;
        const int perWriter = 500;
        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            gate.Wait();
            for (int i = 0; i < perWriter; i++)
            {
                // Interleaved revisions, so writers constantly race with older and newer values.
                holder.Publish(Snap((long)i * writers + w));
                long seen = holder.Current!.Revision;
                Assert.True(seen >= 0);
            }
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);

        Assert.Equal((long)(perWriter - 1) * writers + (writers - 1), holder.Current!.Revision);
    }

    [Fact]
    public async Task Readers_never_see_revision_go_backwards()
    {
        var holder = new KodoSnapshotHolder();
        holder.Publish(Snap(0));
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            long last = -1;
            while (!cts.IsCancellationRequested)
            {
                long now = holder.Current!.Revision;
                Assert.True(now >= last);
                last = now;
            }
        }, TestContext.Current.CancellationToken);
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 1; i <= 2000; i++)
            {
                holder.Publish(Snap(i));
            }
        })).ToArray();
        await Task.WhenAll(writers);
        await cts.CancelAsync();
        await reader;
        Assert.Equal(2000, holder.Current!.Revision);
    }

    [Fact]
    public async Task Registration_shares_one_holder_and_defaults_to_no_incidents()
    {
        var services = new ServiceCollection().AddBazaarKodo().BuildServiceProvider();
        var publisher = services.GetRequiredService<IKodoSnapshotPublisher>();
        var provider = services.GetRequiredService<IKodoSnapshotProvider>();
        publisher.Publish(Snap(1));
        Assert.Equal(1, provider.Current!.Revision);
        Assert.Empty(await services.GetRequiredService<IKodoIncidentSource>().ListOpenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Component_manifest_is_kodo()
    {
        var component = new KodoComponent();
        Assert.Equal("kodo", component.Manifest.Id);
        var services = new ServiceCollection();
        component.AddServices(services, new ConfigurationBuilder().Build());
        Assert.NotNull(services.BuildServiceProvider().GetService<IKodoSnapshotProvider>());
    }
}
