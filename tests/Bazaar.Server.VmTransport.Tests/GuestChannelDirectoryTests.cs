using Bazaar.Server.VmMounts.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>A channel that does nothing but report whether it is open and whether it was disposed.</summary>
internal sealed class FakeLifetimeChannel : IGuestChannel, IGuestChannelLifetime, IAsyncDisposable
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public bool IsOpen => !_done.Task.IsCompleted;

    public Task Completion => _done.Task;

    public int DisposeCount => Volatile.Read(ref _disposed);

    public void End() => _done.TrySetResult();

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        End();
        return ValueTask.CompletedTask;
    }

    public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct) => throw new NotSupportedException();

    public Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct) => throw new NotSupportedException();

    public Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteFileAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GuestDirectoryEntry>> ListFilesAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A channel with no lifetime signal.</summary>
internal sealed class NoLifetimeChannel : IGuestChannel
{
    public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct) => throw new NotSupportedException();

    public Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct) => throw new NotSupportedException();

    public Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteFileAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GuestDirectoryEntry>> ListFilesAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeVmLookup : IAgentVmLookup
{
    public Dictionary<Guid, List<AgentVmInfo>> Vms { get; } = [];

    public ValueTask<IReadOnlyList<AgentVmInfo>> ListAsync(Guid agentId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AgentVmInfo>>(Vms.TryGetValue(agentId, out var list) ? [.. list] : []);
}

public class GuestChannelDirectoryTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherAgent = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _linuxVm = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _winVm = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private const string _linuxName = "bazaar-aaaaaaaa000000000000000000000001";
    private const string _winName = "bazaar-aaaaaaaa000000000000000000000002";

    private readonly FakeVmLookup _lookup = new();
    private readonly GuestChannelRegistry _registry = new();
    private readonly GuestChannelDirectory _directory;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public GuestChannelDirectoryTests()
    {
        _directory = new GuestChannelDirectory(_registry, _lookup);
        _lookup.Vms[_agent] =
        [
            new AgentVmInfo(_linuxVm, GuestOs.Linux, _linuxName, false),
            new AgentVmInfo(_winVm, GuestOs.Windows, _winName, false),
        ];
    }

    private static string Id(Guid g) => g.ToString("D");

    [Fact]
    public async Task A_registered_open_channel_is_served_by_agent_os_and_vm()
    {
        var channel = new FakeLifetimeChannel();
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, channel);

        var guest = await _directory.TryGetAsync(Id(_agent), GuestOs.Linux, _ct);

        Assert.NotNull(guest);
        Assert.Same(channel, guest.Channel);
        Assert.Equal(_linuxName, guest.VmName);
        Assert.Equal(_linuxVm, guest.VmId);
        Assert.Equal(Id(_agent), guest.AgentId);
        Assert.Same(channel, await _directory.TryGetByVmAsync(_linuxVm, _ct));
        Assert.Null(await _directory.TryGetAsync(Id(_agent), GuestOs.Windows, _ct));
    }

    [Fact]
    public async Task List_is_ordered_by_os_then_name()
    {
        _registry.Register(_winVm, Id(_agent), _winName, GuestOs.Windows, new FakeLifetimeChannel());
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());

        var list = await _directory.ListAsync(Id(_agent), _ct);

        Assert.Equal([GuestOs.Linux, GuestOs.Windows], list.Select(g => g.Os));
    }

    [Fact]
    public async Task An_agent_id_that_is_not_a_guid_finds_nothing()
    {
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Null(await _directory.TryGetAsync("not-a-guid", GuestOs.Linux, _ct));
        Assert.Empty(await _directory.ListAsync("", _ct));
        Assert.Empty(await _directory.ListAsync(Guid.Empty.ToString(), _ct));
    }

    [Fact]
    public async Task Replacing_a_channel_closes_the_old_one_and_serves_the_new()
    {
        var first = new FakeLifetimeChannel();
        var second = new FakeLifetimeChannel();
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, first);

        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, second);

        await Eventually(() => first.DisposeCount == 1);
        Assert.Equal(0, second.DisposeCount);
        Assert.Same(second, await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task The_old_channel_ending_after_a_replace_does_not_remove_the_new_entry()
    {
        var first = new FakeLifetimeChannel();
        var second = new FakeLifetimeChannel();
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, first);
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, second);
        await Eventually(() => first.DisposeCount == 1);
        await Task.Delay(20, _ct);

        Assert.Same(second, await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public void Unregister_removes_only_the_same_instance()
    {
        var current = new FakeLifetimeChannel();
        var stranger = new FakeLifetimeChannel();
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, current);

        Assert.False(_registry.Unregister(_linuxVm, stranger));
        Assert.True(_registry.Unregister(_linuxVm, current));
        Assert.False(_registry.Unregister(_linuxVm, current));
        Assert.Equal(0, current.DisposeCount);
    }

    [Fact]
    public async Task An_ended_channel_is_never_served_and_is_removed()
    {
        var channel = new FakeLifetimeChannel();
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, channel);

        channel.End();

        Assert.Null(await _directory.TryGetByVmAsync(_linuxVm, _ct));
        Assert.Null(await _directory.TryGetAsync(Id(_agent), GuestOs.Linux, _ct));
        await Eventually(() => !_registry.Unregister(_linuxVm, channel));
    }

    [Fact]
    public async Task A_vm_the_agent_does_not_own_is_not_served()
    {
        // The registry says the VM belongs to the other agent, the store says it belongs to _agent.
        _registry.Register(_linuxVm, Id(_otherAgent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Null(await _directory.TryGetAsync(Id(_agent), GuestOs.Linux, _ct));
        Assert.Null(await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task A_vm_moved_to_another_agent_in_the_store_is_not_served_to_the_old_one()
    {
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());
        _lookup.Vms[_agent].RemoveAll(v => v.VmId == _linuxVm);

        Assert.Null(await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task A_retired_vm_is_not_served()
    {
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());
        _lookup.Vms[_agent][0] = _lookup.Vms[_agent][0] with { Retired = true };

        Assert.Null(await _directory.TryGetAsync(Id(_agent), GuestOs.Linux, _ct));
        Assert.Null(await _directory.TryGetByVmAsync(_linuxVm, _ct));
        Assert.Empty(await _directory.ListAsync(Id(_agent), _ct));
    }

    [Fact]
    public async Task A_name_or_os_that_differs_from_the_store_is_not_served()
    {
        _registry.Register(_linuxVm, Id(_agent), "bazaar-other", GuestOs.Linux, new FakeLifetimeChannel());
        _registry.Register(_winVm, Id(_agent), _winName, GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Empty(await _directory.ListAsync(Id(_agent), _ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad\n")]
    [InlineData("../x")]
    [InlineData("a;b")]
    [InlineData("-leading")]
    public void The_vm_name_pattern_is_enforced_at_registration(string name)
    {
        Assert.Throws<ArgumentException>(() => _registry.Register(_linuxVm, Id(_agent), name, GuestOs.Linux, new FakeLifetimeChannel()));
    }

    [Fact]
    public async Task A_store_name_that_fails_the_pattern_is_not_served()
    {
        _lookup.Vms[_agent][0] = _lookup.Vms[_agent][0] with { VmName = "bad name" };
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Empty(await _directory.ListAsync(Id(_agent), _ct));
    }

    [Fact]
    public void Registration_refuses_bad_input()
    {
        var ok = new FakeLifetimeChannel();
        Assert.Throws<ArgumentException>(() => _registry.Register(_linuxVm, "nope", _linuxName, GuestOs.Linux, ok));
        Assert.Throws<ArgumentException>(() => _registry.Register(Guid.Empty, Id(_agent), _linuxName, GuestOs.Linux, ok));
        Assert.Throws<ArgumentException>(() => _registry.Register(_linuxVm, Id(_agent), _linuxName, (GuestOs)99, ok));
        Assert.Throws<ArgumentException>(() => _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new NoLifetimeChannel()));
        var ended = new FakeLifetimeChannel();
        ended.End();
        Assert.Throws<InvalidOperationException>(() => _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, ended));
    }

    [Fact]
    public async Task Two_live_vms_of_one_os_are_ambiguous_and_not_served_by_os()
    {
        var second = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
        _lookup.Vms[_agent].Add(new AgentVmInfo(second, GuestOs.Linux, "bazaar-aaaaaaaa000000000000000000000003", false));
        _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());
        _registry.Register(second, Id(_agent), "bazaar-aaaaaaaa000000000000000000000003", GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Null(await _directory.TryGetAsync(Id(_agent), GuestOs.Linux, _ct));
        Assert.Equal(2, (await _directory.ListAsync(Id(_agent), _ct)).Count);
    }

    [Fact]
    public async Task Concurrent_register_and_replace_leaves_exactly_one_open_channel_and_closes_the_rest()
    {
        const int writers = 32;
        const int rounds = 50;
        var all = new List<FakeLifetimeChannel>();
        var gate = new object();

        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                var channel = new FakeLifetimeChannel();
                lock (gate)
                {
                    all.Add(channel);
                }

                _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, channel);
            }
        }, _ct)));

        var served = await _directory.TryGetByVmAsync(_linuxVm, _ct);
        Assert.NotNull(served);
        await Eventually(() => all.Count(c => c.DisposeCount == 0) == 1);
        Assert.Same(all.Single(c => c.DisposeCount == 0), served);
        Assert.All(all, c => Assert.True(c.DisposeCount <= 1));
    }

    [Fact]
    public async Task Concurrent_end_and_register_never_serve_an_ended_channel()
    {
        for (var i = 0; i < 200; i++)
        {
            var channel = new FakeLifetimeChannel();
            _registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, channel);
            await Task.WhenAll(Task.Run(channel.End, _ct), Task.Run(async () =>
            {
                var got = await _directory.TryGetByVmAsync(_linuxVm, _ct);
                Assert.True(got is null || got is FakeLifetimeChannel);
            }, _ct));
            Assert.Null(await _directory.TryGetByVmAsync(_linuxVm, _ct));
        }
    }

    [Fact]
    public async Task The_registrar_registers_a_channel_for_a_live_vm_of_the_agent()
    {
        var registrar = new GuestChannelRegistrar(_registry, _lookup);
        var channel = new FakeLifetimeChannel();

        var guest = await registrar.RegisterOpenedAsync(_linuxVm, Id(_agent), new GuestHandshake(_linuxName, "n"), channel, _ct);

        Assert.Equal(_linuxName, guest.VmName);
        Assert.Same(channel, await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task The_registrar_accepts_a_hello_that_names_the_environment_vm_id()
    {
        var registrar = new GuestChannelRegistrar(_registry, _lookup);

        await registrar.RegisterOpenedAsync(_linuxVm, Id(_agent), new GuestHandshake(_linuxVm.ToString("D"), "n"), new FakeLifetimeChannel(), _ct);

        Assert.NotNull(await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task The_registrar_refuses_and_disposes_when_the_hello_names_another_vm()
    {
        var registrar = new GuestChannelRegistrar(_registry, _lookup);
        var channel = new FakeLifetimeChannel();

        await Assert.ThrowsAsync<GuestChannelRegistrationException>(
            () => registrar.RegisterOpenedAsync(_linuxVm, Id(_agent), new GuestHandshake(_winName, "n"), channel, _ct));

        Assert.Equal(1, channel.DisposeCount);
        Assert.Null(await _directory.TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task The_registrar_refuses_a_vm_that_is_not_the_agents_or_is_retired()
    {
        var registrar = new GuestChannelRegistrar(_registry, _lookup);
        var foreign = new FakeLifetimeChannel();
        var retired = new FakeLifetimeChannel();
        _lookup.Vms[_agent][1] = _lookup.Vms[_agent][1] with { Retired = true };

        await Assert.ThrowsAsync<GuestChannelRegistrationException>(
            () => registrar.RegisterOpenedAsync(_linuxVm, Id(_otherAgent), new GuestHandshake(_linuxName, "n"), foreign, _ct));
        await Assert.ThrowsAsync<GuestChannelRegistrationException>(
            () => registrar.RegisterOpenedAsync(_winVm, Id(_agent), new GuestHandshake(_winName, "n"), retired, _ct));

        Assert.Equal(1, foreign.DisposeCount);
        Assert.Equal(1, retired.DisposeCount);
    }

    [Fact]
    public async Task Without_a_lookup_nothing_is_served_and_the_registrar_refuses()
    {
        var registry = new GuestChannelRegistry();
        var none = new NoAgentVmLookup();
        var directory = new GuestChannelDirectory(registry, none);
        registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Null(await directory.TryGetByVmAsync(_linuxVm, _ct));
        await Assert.ThrowsAsync<GuestChannelRegistrationException>(() => new GuestChannelRegistrar(registry, none)
            .RegisterOpenedAsync(_linuxVm, Id(_agent), new GuestHandshake(_linuxName, "n"), new FakeLifetimeChannel(), _ct));
    }

    [Fact]
    public async Task The_module_registers_the_directory_and_defaults_to_serving_nothing()
    {
        var services = new ServiceCollection().AddBazaarVmTransport();
        await using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<IGuestChannelRegistry>();
        registry.Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, new FakeLifetimeChannel());

        Assert.Same(sp.GetRequiredService<GuestChannelRegistry>(), registry);
        Assert.NotNull(sp.GetRequiredService<GuestChannelRegistrar>());
        Assert.IsType<NoAgentVmLookup>(sp.GetRequiredService<IAgentVmLookup>());
        Assert.Null(await sp.GetRequiredService<IGuestChannelDirectory>().TryGetByVmAsync(_linuxVm, _ct));
    }

    [Fact]
    public async Task A_host_lookup_registered_first_wins_and_the_directory_serves_through_it()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentVmLookup>(_lookup);
        services.AddBazaarVmTransport();
        await using var sp = services.BuildServiceProvider();
        var channel = new FakeLifetimeChannel();

        sp.GetRequiredService<IGuestChannelRegistry>().Register(_linuxVm, Id(_agent), _linuxName, GuestOs.Linux, channel);

        Assert.Same(channel, await sp.GetRequiredService<IGuestChannelDirectory>().TryGetByVmAsync(_linuxVm, _ct));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
