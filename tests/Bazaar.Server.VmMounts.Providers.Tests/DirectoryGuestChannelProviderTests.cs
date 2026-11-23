using Bazaar.Server.VmMounts.Providers.Guest;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class DirectoryGuestChannelProviderTests
{
    private sealed class FakeDirectory(IGuestChannel? channel) : IGuestChannelDirectory
    {
        public Guid? Asked { get; private set; }

        public ValueTask<AgentGuest?> TryGetAsync(string agentId, VmMounts.Abstractions.GuestOs os, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentGuest>> ListAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IGuestChannel?> TryGetByVmAsync(Guid vmId, CancellationToken ct)
        {
            Asked = vmId;
            return ValueTask.FromResult(channel);
        }
    }

    [Fact]
    public async Task It_returns_the_directory_channel_for_the_vm()
    {
        var channel = new FakeGuestChannel();
        var directory = new FakeDirectory(channel);
        var vm = Guid.NewGuid();

        var got = await new DirectoryGuestChannelProvider(directory).TryGetAsync(vm, TestContext.Current.CancellationToken);

        Assert.Same(channel, got);
        Assert.Equal(vm, directory.Asked);
    }

    [Fact]
    public async Task It_returns_null_when_the_directory_has_none()
    {
        var got = await new DirectoryGuestChannelProvider(new FakeDirectory(null)).TryGetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(got);
    }

    [Fact]
    public void The_registration_uses_the_directory_when_one_is_composed_in_either_order()
    {
        var before = new ServiceCollection();
        before.AddSingleton<IGuestChannelDirectory>(new FakeDirectory(null));
        before.AddBazaarVmMountProviders();
        var after = new ServiceCollection();
        after.AddBazaarVmMountProviders();
        after.AddSingleton<IGuestChannelDirectory>(new FakeDirectory(null));

        using var a = before.BuildServiceProvider();
        using var b = after.BuildServiceProvider();

        Assert.IsType<DirectoryGuestChannelProvider>(a.GetRequiredService<IGuestChannelProvider>());
        Assert.IsType<DirectoryGuestChannelProvider>(b.GetRequiredService<IGuestChannelProvider>());
    }

    [Fact]
    public void Without_a_directory_the_provider_stays_unavailable()
    {
        var services = new ServiceCollection();
        services.AddBazaarVmMountProviders();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<UnavailableGuestChannelProvider>(sp.GetRequiredService<IGuestChannelProvider>());
    }
}
