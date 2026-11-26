using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Fakes;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.WorkLife;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Presence.Tests;

/// <summary>Component registration, option parsing and the hosted rebuild and sweep.</summary>
public class ComponentAndOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Component_manifest_is_the_presence_feature_with_no_dependencies()
    {
        var component = new PresenceComponent();

        Assert.Equal("presence", component.Manifest.Id);
        Assert.Empty(component.Manifest.DependsOn);
    }

    [Fact]
    public void Registration_resolves_the_projector_publisher_handler_and_hosted_service()
    {
        var services = new ServiceCollection();
        new PresenceComponent().AddServices(services, Config());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ActivityProjector>(provider.GetRequiredService<IActivityProjector>());
        Assert.IsType<InMemoryPresenceStore>(provider.GetRequiredService<IPresenceStore>());
        Assert.IsType<PresencePublisher>(provider.GetRequiredService<IPresencePublisher>());
        Assert.Contains(provider.GetServices<IWireRequestHandler>(), h => h.Type == "presence.snapshot");
        Assert.Contains(provider.GetServices<IHostedService>(), h => h is PresenceHostedService);
    }

    [Fact]
    public void Registration_adds_the_snapshot_cache_when_a_cache_provider_exists()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICacheProvider>(new FakeCacheProvider());
        services.AddBazaarPresence(Config());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<CacheSnapshotPresenceStore>(provider.GetRequiredService<IPresenceStore>());
    }

    [Fact]
    public void Options_default_to_the_doc_values()
    {
        var o = PresenceOptionsReader.Read(Config().GetSection(PresenceOptions.SectionName));

        Assert.Equal("working", o.IdleLabel);
        Assert.Equal(ActivityLabelMode.KindsAndLabel, o.ActivityLabelMode);
        Assert.Equal(TimeSpan.FromMinutes(10), o.UnknownAfter);
    }

    [Fact]
    public void Options_read_the_section_and_refuse_bad_values()
    {
        var good = PresenceOptionsReader.Read(Config(("Bazaar:Presence:IdleLabel", "idle"), ("Bazaar:Presence:ActivityLabelMode", "KindsOnly"), ("Bazaar:Presence:UnknownAfterS", "60"))
            .GetSection(PresenceOptions.SectionName));
        Assert.Equal("idle", good.IdleLabel);
        Assert.Equal(ActivityLabelMode.KindsOnly, good.ActivityLabelMode);
        Assert.Equal(TimeSpan.FromSeconds(60), good.UnknownAfter);

        Assert.Throws<InvalidOperationException>(() => PresenceOptionsReader.Read(Config(("Bazaar:Presence:IdleLabel", "napping")).GetSection(PresenceOptions.SectionName)));
        Assert.Throws<InvalidOperationException>(() => PresenceOptionsReader.Read(Config(("Bazaar:Presence:UnknownAfterS", "0")).GetSection(PresenceOptions.SectionName)));
        Assert.Throws<InvalidOperationException>(() => PresenceOptionsReader.Read(Config(("Bazaar:Presence:ActivityLabelMode", "all")).GetSection(PresenceOptions.SectionName)));
    }

    private sealed class Seeds(params PresenceSeed[] seeds) : IPresenceSeedSource
    {
        public ValueTask<IReadOnlyList<PresenceSeed>> LoadAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PresenceSeed>>(seeds);
    }

    [Fact]
    public async Task Hosted_service_rebuilds_from_the_seed_source_then_marks_quiet_rows_unknown()
    {
        var rig = new PresenceRig(new PresenceOptions { SweepInterval = TimeSpan.FromSeconds(30) });
        var service = new PresenceHostedService(rig.Projector, rig.Options, rig.Time, new Seeds(new PresenceSeed(PresenceRig.Ada, PresenceAvailability.OnShift)));

        await service.StartAsync(CancellationToken.None);
        await WaitFor(async () => await rig.Store.GetAsync(PresenceRig.Ada, TestContext.Current.CancellationToken) is not null);

        Assert.Equal("working", (await rig.RowAsync(PresenceRig.Ada)).Presence);

        for (var i = 0; i < 40; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await WaitFor(async () => (await rig.RowAsync(PresenceRig.Ada)).Presence == PresenceRow.UnknownLabel);
        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Condition not reached.");
    }
}
