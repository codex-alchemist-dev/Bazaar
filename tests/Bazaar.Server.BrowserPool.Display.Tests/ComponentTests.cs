using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.BrowserPool.Display.Tests;

public class ComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(Action<IServiceCollection>? before = null, Action<IServiceCollection>? after = null, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        new BrowserPoolDisplayComponent().AddServices(services, config ?? Config());
        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static DisplayTarget Target() => new("agent-a", Guid.NewGuid(), DisplayGuestOs.Linux, "pool", "0123456789abcdef0123456789abcdef");

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new BrowserPoolDisplayComponent().Manifest;

        Assert.Equal("browser-pool-display", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["vm-display", "browser-pool"], m.DependsOn);
        Assert.Equal(["browser-pool:page-driver"], m.Provides);
    }

    [Fact]
    public async Task The_driver_resolves_without_a_host_and_supports_nothing()
    {
        await using ServiceProvider sp = Build();

        IBrowserPageDriver driver = sp.GetRequiredService<IBrowserPageDriver>();

        Assert.IsType<PoolBrowserPageDriver>(driver);
        Assert.Same(driver, sp.GetRequiredService<IBrowserPageDriver>());
        Assert.False(driver.Supports(Target()));
        Assert.Single(sp.GetServices<IBrowserPageDriver>());
    }

    [Fact]
    public async Task A_registered_host_is_used_in_either_order()
    {
        var pipes = new FakePipes();
        var browser = new FakeBrowser();
        await using (browser)
        {
            pipes.Add(Target().BrowserSessionId!, browser.Pipe);
            DisplayTarget target = Target();

            await using ServiceProvider before = Build(before: s => s.AddSingleton<IPoolDevToolsPipes>(pipes));
            Assert.True(before.GetRequiredService<IBrowserPageDriver>().Supports(target));

            await using ServiceProvider after = Build(after: s =>
            {
                s.RemoveAll<IPoolDevToolsPipes>();
                s.AddSingleton<IPoolDevToolsPipes>(pipes);
            });
            Assert.True(after.GetRequiredService<IBrowserPageDriver>().Supports(target));
        }
    }

    [Fact]
    public async Task It_sits_beside_other_drivers_for_the_display_module()
    {
        await using ServiceProvider sp = Build(before: s => s.AddSingleton<IBrowserPageDriver>(new OtherDriver()));

        Assert.Equal(2, sp.GetServices<IBrowserPageDriver>().Count());
    }

    [Fact]
    public void Options_default_when_the_section_is_absent()
    {
        BrowserPageDriverOptions o = BrowserPageDriverOptions.Bind(Config());

        Assert.Equal(TimeSpan.FromSeconds(10), o.CommandTimeout);
        Assert.Equal(32, o.MaxPendingRequests);
        Assert.Equal(32 * 1024 * 1024, o.MaxScreenshotBytes);
        Assert.Equal(200, o.MaxActionsPerBatch);
        Assert.Equal(2000, o.MaxTextLength);
        Assert.Equal(16384, o.MaxCoordinate);
        Assert.Equal(10_000, o.MaxScrollDelta);
    }

    [Fact]
    public async Task Options_take_overrides_and_reach_the_driver_registration()
    {
        IConfiguration config = Config(
            ("Bazaar:BrowserPool:Display:CommandTimeout", "00:00:03"),
            ("Bazaar:BrowserPool:Display:MaxPendingRequests", "4"),
            ("Bazaar:BrowserPool:Display:MaxScreenshotBytes", "1048576"),
            ("Bazaar:BrowserPool:Display:MaxActionsPerBatch", "20"),
            ("Bazaar:BrowserPool:Display:MaxTextLength", "100"),
            ("Bazaar:BrowserPool:Display:MaxCoordinate", "4096"),
            ("Bazaar:BrowserPool:Display:MaxScrollDelta", "500"));

        await using ServiceProvider sp = Build(config: config);
        BrowserPageDriverOptions o = sp.GetRequiredService<BrowserPageDriverOptions>();

        Assert.Equal(TimeSpan.FromSeconds(3), o.CommandTimeout);
        Assert.Equal(4, o.MaxPendingRequests);
        Assert.Equal(1_048_576, o.MaxScreenshotBytes);
        Assert.Equal(20, o.MaxActionsPerBatch);
        Assert.Equal(100, o.MaxTextLength);
        Assert.Equal(4096, o.MaxCoordinate);
        Assert.Equal(500, o.MaxScrollDelta);
    }

    [Theory]
    [InlineData("MaxPendingRequests", "-7")]
    [InlineData("MaxPendingRequests", "5000")]
    [InlineData("MaxScreenshotBytes", "999999999")]
    [InlineData("MaxTextLength", "-1")]
    [InlineData("CommandTimeout", "00:00:00")]
    [InlineData("CommandTimeout", "01:00:00")]
    public void Out_of_range_options_throw_at_startup_without_echoing_the_value(string key, string value)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => Build(config: Config(($"Bazaar:BrowserPool:Display:{key}", value))));

        Assert.DoesNotContain(value, ex.Message);
    }

    [Theory]
    [InlineData("MaxCoordinate", "wide")]
    [InlineData("CommandTimeout", "soon")]
    public void Unreadable_options_throw_at_startup_without_echoing_the_value(string key, string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Build(config: Config(($"Bazaar:BrowserPool:Display:{key}", value))));

        Assert.DoesNotContain(value, ex.Message);
    }

    private sealed class OtherDriver : IBrowserPageDriver
    {
        public bool Supports(DisplayTarget target) => false;

        public Task<byte[]> CaptureScreenshotPngAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task DispatchInputAsync(string sessionId, IReadOnlyList<InputAction> actions, CancellationToken ct) => throw new NotSupportedException();
    }
}
