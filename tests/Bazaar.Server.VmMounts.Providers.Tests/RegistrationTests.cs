using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Canaries;
using Bazaar.Server.VmMounts.Providers.Guest;
using Bazaar.Server.VmMounts.Providers.Local;
using Bazaar.Server.VmMounts.Providers.Smb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class RegistrationTests : IDisposable
{
    private readonly MountEnv _env = new();

    public void Dispose() => _env.Dispose();

    private ServiceCollection Base()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_env.Options);
        services.AddSingleton<IGuestMountLayoutProvider>(_env.Options);
        services.AddSingleton<IAuditLog>(new FakeAudit());
        services.AddSingleton<IScopedMountStore>(new FakeStore());
        return services;
    }

    [Fact]
    public void The_three_ports_are_registered_and_resolve()
    {
        var services = Base();
        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        Assert.NotNull(sp.GetRequiredService<IMountProvider>());
        Assert.IsType<GuestChannelMountAdapter>(sp.GetRequiredService<IMountGuestChannel>());
        Assert.IsType<ContainingCanarySink>(sp.GetRequiredService<ICanarySink>());
        Assert.Same(sp.GetRequiredService<ContainingCanarySink>(), sp.GetRequiredService<ICanarySink>());
    }

    [Fact]
    public void The_provider_is_chosen_by_os_in_the_factory_only()
    {
        var services = Base();
        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<SmbMountProvider>(MountProviderFactory.Create(sp, isWindows: true));
        Assert.IsType<DirectoryMountProvider>(MountProviderFactory.Create(sp, isWindows: false));
        Assert.IsType(OperatingSystem.IsWindows() ? typeof(SmbMountProvider) : typeof(DirectoryMountProvider), sp.GetRequiredService<IMountProvider>());
    }

    [Fact]
    public void A_host_override_registered_first_wins()
    {
        var provider = new DirectoryMountProvider(_env.Options, Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectoryMountProvider>.Instance);
        var channel = new GuestChannelMountAdapter(new FakeChannelProvider(null), new FakeStore(), _env.Options, _env.Options);
        var sink = new OverrideSink();
        var services = Base();
        services.AddSingleton<IMountProvider>(provider);
        services.AddSingleton<IMountGuestChannel>(channel);
        services.AddSingleton<ICanarySink>(sink);

        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider();

        Assert.Same(provider, sp.GetRequiredService<IMountProvider>());
        Assert.Same(channel, sp.GetRequiredService<IMountGuestChannel>());
        Assert.Same(sink, sp.GetRequiredService<ICanarySink>());
    }

    [Fact]
    public void Calling_it_twice_does_not_duplicate_ports()
    {
        var services = Base();
        services.AddBazaarVmMountProviders();
        services.AddBazaarVmMountProviders();

        Assert.Single(services, d => d.ServiceType == typeof(IMountProvider));
        Assert.Single(services, d => d.ServiceType == typeof(IMountGuestChannel));
        Assert.Single(services, d => d.ServiceType == typeof(ICanarySink));
    }

    [Fact]
    public async Task The_host_ports_the_module_cannot_supply_fail_closed()
    {
        var services = Base();
        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sp.GetRequiredService<IVmShareIdentity>().EnsureAccountAsync(Guid.NewGuid(), Guid.NewGuid(), MountKind.Soul, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sp.GetRequiredService<IVmShareEndpoint>().GetHostAddressAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await sp.GetRequiredService<IGuestChannelProvider>().TryGetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_background_services_are_registered()
    {
        var services = Base();
        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider();

        var hosted = sp.GetServices<IHostedService>().Select(h => h.GetType().Name).ToArray();
        Assert.Contains("CanaryTripRetryService", hosted);
        Assert.Contains("SmbShareReconcileService", hosted);
    }

    [Fact]
    public async Task Reconcile_service_does_nothing_without_the_mounts_module_or_off_the_smb_provider()
    {
        // Without VmMountOptions the mounts module is not composed: the service ends at once and resolves nothing else.
        var bare = new ServiceCollection();
        bare.AddLogging();
        bare.AddSingleton<IAuditLog>(new FakeAudit());
        bare.AddBazaarVmMountProviders();
        using var spBare = bare.BuildServiceProvider();
        var service = spBare.GetServices<IHostedService>().Single(h => h.GetType().Name == "SmbShareReconcileService");
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // With the module but a directory provider override, it also ends without touching the host.
        var services = Base();
        services.AddSingleton<IMountProvider>(new DirectoryMountProvider(_env.Options, Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectoryMountProvider>.Instance));
        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider();
        var other = sp.GetServices<IHostedService>().Single(h => h.GetType().Name == "SmbShareReconcileService");
        await other.StartAsync(TestContext.Current.CancellationToken);
        await other.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class OverrideSink : ICanarySink
    {
        public ValueTask ReportCanaryUseAsync(CanaryUseReport report, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
