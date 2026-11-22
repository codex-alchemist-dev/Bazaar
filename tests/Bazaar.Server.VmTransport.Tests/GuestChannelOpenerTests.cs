using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bazaar.Contracts.Vms;
using Bazaar.Server.VmMounts.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmTransport.Tests;

internal sealed class FakeOpenTargets : IGuestOpenTargets
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, GuestOpenTarget> _running = [];

    public bool ThrowOnList { get; set; }

    public int ListCalls { get; private set; }

    public void Set(GuestOpenTarget target)
    {
        lock (_gate)
        {
            _running[target.VmId] = target;
        }
    }

    public void Remove(Guid vmId)
    {
        lock (_gate)
        {
            _running.Remove(vmId);
        }
    }

    public ValueTask<IReadOnlyList<GuestOpenTarget>> ListRunningAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ListCalls++;
            if (ThrowOnList)
            {
                throw new InvalidOperationException("the store is down");
            }

            return ValueTask.FromResult<IReadOnlyList<GuestOpenTarget>>([.. _running.Values]);
        }
    }

    public ValueTask<GuestOpenTarget?> GetAsync(Guid vmId, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_running.GetValueOrDefault(vmId));
        }
    }
}

/// <summary>Wraps a listener and counts waits, so a test knows when the opener is waiting for a peer.</summary>
internal sealed class SpyListener(IGuestConnectionListener inner) : IGuestConnectionListener
{
    private int _calls;

    public GuestTransportKind Kind => inner.Kind;

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<string> Peers { get; } = [];

    public Func<string, Exception?>? FailWith { get; set; }

    public ValueTask<GuestConnection> AcceptAsync(string vmId, CancellationToken ct)
    {
        Peers.Enqueue(vmId);
        if (FailWith?.Invoke(vmId) is { } failure)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromException<GuestConnection>(failure);
        }

        var accepted = inner.AcceptAsync(vmId, ct);
        Interlocked.Increment(ref _calls);
        return accepted;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));

    public IEnumerable<string> Messages(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);
}

public sealed class GuestChannelOpenerTests : IAsyncDisposable
{
    private const string _peer1 = "bazaar-aaaaaaaa000000000000000000000001";
    private const string _peer2 = "bazaar-aaaaaaaa000000000000000000000002";
    private const string _peer3 = "bazaar-aaaaaaaa000000000000000000000003";

    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _vm1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _vm2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid _vm3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeOpenTargets _targets = new();
    private readonly FakeVmLookup _lookup = new();
    private readonly LoopbackListener _loopback = new();
    private readonly SpyListener _spy;
    private readonly GuestChannelRegistry _registry = new();
    private readonly CapturingLogger<GuestChannelOpener> _log = new();
    private readonly List<LoopbackGuestEnd> _guests = [];
    private readonly List<GuestChannelOpener> _openers = [];

    public GuestChannelOpenerTests()
    {
        _spy = new SpyListener(_loopback);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var opener in _openers)
        {
            await opener.StopAsync(CancellationToken.None);
            opener.Dispose();
        }

        foreach (var guest in _guests)
        {
            await guest.ToHost.DisposeAsync();
            await guest.FromHost.DisposeAsync();
        }

        await _loopback.DisposeAsync();
    }

    private static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private GuestOpenTarget AddVm(Guid vmId, string peer, string nonce, GuestOs os = GuestOs.Linux, bool inLookup = true)
    {
        var target = new GuestOpenTarget(vmId, _agent.ToString("D"), peer, os, nonce);
        _targets.Set(target);
        if (inLookup)
        {
            if (!_lookup.Vms.TryGetValue(_agent, out var list))
            {
                _lookup.Vms[_agent] = list = [];
            }

            list.Add(new AgentVmInfo(vmId, os, peer, false));
        }

        return target;
    }

    private GuestChannelOpener NewOpener(GuestChannelOpenerOptions? options = null)
    {
        var channelOptions = new GuestChannelOptions { HelloTimeout = TimeSpan.FromSeconds(10) };
        var factory = new GuestChannelFactory(channelOptions, TimeProvider.System);
        var registrar = new GuestChannelRegistrar(_registry, _lookup);
        var opener = new GuestChannelOpener(
            _targets, factory, _spy, registrar, _registry, options ?? Options(), _time, _log);
        _openers.Add(opener);
        return opener;
    }

    private static GuestChannelOpenerOptions Options(int concurrency = 4, TimeSpan? maxBackoff = null) => new()
    {
        Jitter = 0,
        MaxConcurrentOpens = concurrency,
        InitialBackoff = TimeSpan.FromSeconds(1),
        BadHelloBackoff = TimeSpan.FromSeconds(5),
        MaxBackoff = maxBackoff ?? TimeSpan.FromSeconds(60),
        AttemptTimeout = TimeSpan.FromMinutes(10),
    };

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound.Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not reached in time.");
            }

            await Task.Delay(10, _ct);
        }
    }

    private async Task DialAsync(string peer, string vmId, string nonce)
    {
        var end = _loopback.Connect(peer);
        _guests.Add(end);
        await GuestFrameCodec.WriteAsync(end.ToHost, new Hello("test", vmId, nonce, GuestFrameCodec.Version), _ct);
    }

    [Fact]
    public async Task Happy_path_registers_the_channel_once()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);

        // The channel is live: later polls start nothing.
        await opener.PollOnceAsync(_ct);
        await opener.PollOnceAsync(_ct);
        Assert.Equal(1, _spy.Calls);
        await WaitForAsync(() => opener.ActiveAttempts == 0);
        var entry = _registry.Find(_vm1)!;
        Assert.Equal(_agent, entry.AgentId);
        Assert.Equal(_peer1, entry.VmName);
        Assert.Equal(GuestOs.Linux, entry.Os);
        Assert.Contains(_log.Messages(LogLevel.Information), m => m.Contains(_vm1.ToString("D")));
    }

    [Fact]
    public async Task The_directory_serves_the_opened_channel()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var directory = new GuestChannelDirectory(_registry, _lookup);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);

        Assert.NotNull(await directory.TryGetAsync(_agent.ToString("D"), GuestOs.Linux, _ct));
    }

    [Fact]
    public async Task A_wrong_nonce_is_dropped_and_never_registered()
    {
        var nonce = NewNonce();
        var wrong = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), wrong);
        await WaitForAsync(() => _log.Messages(LogLevel.Warning).Any());
        await WaitForAsync(() => opener.ActiveAttempts == 0);

        Assert.Null(_registry.Find(_vm1));
        var warning = Assert.Single(_log.Messages(LogLevel.Warning));
        Assert.Contains(_vm1.ToString("D"), warning);
        AssertNoSecret(nonce, wrong);
    }

    [Fact]
    public async Task A_wrong_vm_id_is_dropped_and_never_registered()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm2.ToString("D"), nonce);
        await WaitForAsync(() => _log.Messages(LogLevel.Warning).Any());
        await WaitForAsync(() => opener.ActiveAttempts == 0);

        Assert.Null(_registry.Find(_vm1));
        AssertNoSecret(nonce);
    }

    [Fact]
    public async Task Repeated_bad_hellos_are_rate_limited_by_backoff()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), NewNonce());
        await WaitForAsync(() => opener.ActiveAttempts == 0 && _log.Messages(LogLevel.Warning).Any());

        // No new wait is opened inside the bad-Hello back-off (5 s for the first, 10 s for the second, then 20 s).
        _time.Advance(TimeSpan.FromSeconds(4));
        await opener.PollOnceAsync(_ct);
        Assert.Equal(1, _spy.Calls);

        _time.Advance(TimeSpan.FromSeconds(1));
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 2);
        await DialAsync(_peer1, _vm1.ToString("D"), NewNonce());
        await WaitForAsync(() => opener.ActiveAttempts == 0 && _log.Messages(LogLevel.Warning).Count() == 2);

        _time.Advance(TimeSpan.FromSeconds(9));
        await opener.PollOnceAsync(_ct);
        Assert.Equal(2, _spy.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 3);
        Assert.Null(_registry.Find(_vm1));
    }

    [Fact]
    public async Task One_attempt_per_vm_at_a_time()
    {
        AddVm(_vm1, _peer1, NewNonce());
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await opener.PollOnceAsync(_ct);
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls >= 1);
        await opener.PollOnceAsync(_ct);

        Assert.Equal(1, _spy.Calls);
        Assert.Equal(1, opener.ActiveAttempts);
    }

    [Fact]
    public async Task Failures_back_off_exponentially_up_to_the_cap()
    {
        AddVm(_vm1, _peer1, NewNonce());
        _spy.FailWith = _ => new IOException("the transport failed");
        var opener = NewOpener(Options(maxBackoff: TimeSpan.FromSeconds(4)));

        async Task AttemptAsync(int expectedCalls)
        {
            await opener.PollOnceAsync(_ct);
            await WaitForAsync(() => _spy.Calls == expectedCalls && opener.ActiveAttempts == 0);
        }

        await AttemptAsync(1);

        // After failure 1 the wait is 1 s.
        _time.Advance(TimeSpan.FromMilliseconds(999));
        await opener.PollOnceAsync(_ct);
        Assert.Equal(1, _spy.Calls);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await AttemptAsync(2);

        // After failure 2: 2 s.
        _time.Advance(TimeSpan.FromSeconds(1));
        await opener.PollOnceAsync(_ct);
        Assert.Equal(2, _spy.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await AttemptAsync(3);

        // After failure 3: 4 s, the cap. After failure 4 it stays 4 s, not 8.
        _time.Advance(TimeSpan.FromSeconds(4));
        await AttemptAsync(4);
        _time.Advance(TimeSpan.FromSeconds(3));
        await opener.PollOnceAsync(_ct);
        Assert.Equal(4, _spy.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await AttemptAsync(5);
    }

    [Fact]
    public async Task A_success_resets_the_backoff()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var fail = true;
        _spy.FailWith = _ => fail ? new IOException("the transport failed") : null;
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1 && opener.ActiveAttempts == 0);
        _time.Advance(TimeSpan.FromSeconds(1));
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 2 && opener.ActiveAttempts == 0);

        // Two failures in a row: the next wait is 2 s. Let it pass, succeed, then lose the channel.
        fail = false;
        _time.Advance(TimeSpan.FromSeconds(2));
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 3);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);
        await _guests[^1].ToHost.DisposeAsync();
        await WaitForAsync(() => _registry.Find(_vm1) is null);
        await WaitForAsync(() => _log.Messages(LogLevel.Information).Count(m => m.Contains("ended")) == 1);

        // The back-off after a lost channel is the initial 1 s again, not 4 s.
        _time.Advance(TimeSpan.FromSeconds(1));
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 4);
    }

    [Fact]
    public async Task A_lost_channel_is_opened_again_while_the_vm_runs()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);
        var first = _registry.Find(_vm1)!.Channel;

        await _guests[^1].ToHost.DisposeAsync();
        await WaitForAsync(() => _registry.Find(_vm1) is null);
        await WaitForAsync(() => _log.Messages(LogLevel.Information).Any(m => m.Contains("ended")));

        // A flapping guest cannot make the host spin: the re-open waits the initial back-off.
        await opener.PollOnceAsync(_ct);
        Assert.Equal(1, _spy.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 2);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);

        Assert.NotSame(first, _registry.Find(_vm1)!.Channel);
    }

    [Fact]
    public async Task A_vm_that_stops_cancels_its_pending_attempt()
    {
        AddVm(_vm1, _peer1, NewNonce());
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        Assert.Equal(1, opener.ActiveAttempts);

        _targets.Remove(_vm1);
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => opener.ActiveAttempts == 0);

        // The guest that dials in afterwards finds nobody waiting and is dropped.
        await DialAsync(_peer1, _vm1.ToString("D"), "irrelevant");
        Assert.Null(_registry.Find(_vm1));
        Assert.Empty(_log.Messages(LogLevel.Warning));
    }

    [Fact]
    public async Task A_vm_that_stops_has_its_open_channel_disposed()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);
        var channel = (IGuestChannelLifetime)_registry.Find(_vm1)!.Channel;

        _targets.Remove(_vm1);
        await opener.PollOnceAsync(_ct);

        await WaitForAsync(() => !channel.IsOpen);
        Assert.Null(_registry.Find(_vm1));
    }

    [Fact]
    public async Task Stopping_the_service_cancels_attempts()
    {
        AddVm(_vm1, _peer1, NewNonce());
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await opener.StopAsync(_ct);

        Assert.Equal(0, opener.ActiveAttempts);
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        AddVm(_vm1, _peer1, NewNonce());
        AddVm(_vm2, _peer2, NewNonce());
        AddVm(_vm3, _peer3, NewNonce());
        var opener = NewOpener(Options(concurrency: 2));

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 2);
        await opener.PollOnceAsync(_ct);

        Assert.Equal(2, opener.ActiveAttempts);
        Assert.Equal(2, _spy.Calls);
    }

    [Fact]
    public async Task A_timed_out_wait_frees_the_slot_for_the_next_vm()
    {
        AddVm(_vm1, _peer1, NewNonce());
        AddVm(_vm2, _peer2, NewNonce());
        var opener = NewOpener(Options(concurrency: 1) with { AttemptTimeout = TimeSpan.FromSeconds(30) });

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        var first = _spy.Peers.Single();

        _time.Advance(TimeSpan.FromSeconds(31));
        await WaitForAsync(() => opener.ActiveAttempts == 0);
        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 2);

        // The VM that waited longest goes first, so the other one is tried now.
        Assert.NotEqual(first, _spy.Peers.Last());
        Assert.Empty(_log.Messages(LogLevel.Warning));
    }

    [Fact]
    public async Task A_registration_refusal_leaves_nothing_registered_and_logs_without_the_nonce()
    {
        var nonce = NewNonce();
        // The target says the VM runs, but the VM store does not know it for this agent.
        AddVm(_vm1, _peer1, nonce, inLookup: false);
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _log.Messages(LogLevel.Warning).Any());
        await WaitForAsync(() => opener.ActiveAttempts == 0);

        Assert.Null(_registry.Find(_vm1));
        AssertNoSecret(nonce);
    }

    [Fact]
    public async Task A_store_name_that_differs_from_the_target_is_refused()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce, inLookup: false);
        _lookup.Vms[_agent] = [new AgentVmInfo(_vm1, GuestOs.Linux, _peer2, false)];
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _log.Messages(LogLevel.Warning).Any());
        await WaitForAsync(() => opener.ActiveAttempts == 0);

        Assert.Null(_registry.Find(_vm1));
        AssertNoSecret(nonce);
    }

    [Fact]
    public async Task An_unusable_target_is_skipped_and_warned_once()
    {
        _targets.Set(new GuestOpenTarget(_vm1, _agent.ToString("D"), _peer1, GuestOs.Linux, ""));
        _targets.Set(new GuestOpenTarget(_vm2, "not-a-guid", _peer2, GuestOs.Linux, NewNonce()));
        _targets.Set(new GuestOpenTarget(_vm3, _agent.ToString("D"), "../escape", GuestOs.Linux, NewNonce()));
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await opener.PollOnceAsync(_ct);

        Assert.Equal(0, _spy.Calls);
        Assert.Equal(3, _log.Messages(LogLevel.Warning).Count());
    }

    [Fact]
    public async Task A_failing_list_keeps_attempts_in_flight()
    {
        AddVm(_vm1, _peer1, NewNonce());
        var opener = NewOpener();

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        _targets.ThrowOnList = true;
        await opener.PollOnceAsync(_ct);

        Assert.Equal(1, opener.ActiveAttempts);
        Assert.Contains(_log.Messages(LogLevel.Warning), m => m.Contains(nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task The_peer_id_resolver_supplies_the_transport_peer()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var factory = new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System);
        var opener = new GuestChannelOpener(
            _targets, factory, _spy, new GuestChannelRegistrar(_registry, _lookup), _registry, Options(), _time, _log, new FixedPeerResolver("hv-peer-7"));
        _openers.Add(opener);

        await opener.PollOnceAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);

        Assert.Equal("hv-peer-7", _spy.Peers.Single());
    }

    [Fact]
    public void A_target_does_not_print_its_nonce()
    {
        var nonce = NewNonce();
        var target = new GuestOpenTarget(_vm1, _agent.ToString("D"), _peer1, GuestOs.Linux, nonce);

        Assert.DoesNotContain(nonce, target.ToString());
        Assert.Contains(_vm1.ToString("D"), target.ToString());
    }

    [Fact]
    public async Task Invalid_options_are_refused()
    {
        var factory = new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System);
        var registrar = new GuestChannelRegistrar(_registry, _lookup);
        await Task.CompletedTask;

        Assert.Throws<ArgumentOutOfRangeException>(() => new GuestChannelOpener(
            _targets, factory, _spy, registrar, _registry, Options(concurrency: 0), _time, _log));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GuestChannelOpener(
            _targets, factory, _spy, registrar, _registry, Options() with { MaxBackoff = TimeSpan.FromMilliseconds(1) }, _time, _log));
    }

    [Fact]
    public async Task The_hosted_loop_polls_and_opens()
    {
        var nonce = NewNonce();
        AddVm(_vm1, _peer1, nonce);
        var opener = NewOpener();

        await opener.StartAsync(_ct);
        await WaitForAsync(() => _spy.Calls == 1);
        await DialAsync(_peer1, _vm1.ToString("D"), nonce);
        await WaitForAsync(() => _registry.Find(_vm1) is not null);
        await opener.StopAsync(_ct);

        // Stopping disposes what the opener opened.
        await WaitForAsync(() => _registry.Find(_vm1) is null);
    }

    private void AssertNoSecret(params string[] secrets)
    {
        foreach (var entry in _log.Entries)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, entry.Message);
                Assert.DoesNotContain(secret, entry.Exception?.ToString() ?? string.Empty);
            }
        }
    }

    private sealed class FixedPeerResolver(string peer) : IGuestPeerIdResolver
    {
        public ValueTask<string?> ResolveAsync(GuestOpenTarget target, CancellationToken ct) => ValueTask.FromResult<string?>(peer);
    }
}

public sealed class GuestChannelOpenerRegistrationTests
{
    private sealed class NoTargets : IGuestOpenTargets
    {
        public ValueTask<IReadOnlyList<GuestOpenTarget>> ListRunningAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<GuestOpenTarget>>([]);

        public ValueTask<GuestOpenTarget?> GetAsync(Guid vmId, CancellationToken ct) => ValueTask.FromResult<GuestOpenTarget?>(null);
    }

    private static ServiceCollection Base()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGuestConnectionListener>(_ => new LoopbackListener());
        services.AddBazaarVmTransport();
        return services;
    }

    [Fact]
    public async Task Without_targets_no_hosted_service_is_registered()
    {
        var services = Base();
        services.AddBazaarVmGuestOpener();

        await using var provider = services.BuildServiceProvider();
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is GuestChannelOpener);
    }

    [Fact]
    public async Task Without_a_listener_no_hosted_service_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGuestOpenTargets, NoTargets>();
        services.AddBazaarVmTransport();
        // A test host on a platform with a default listener registers it; remove it so the case is the same everywhere.
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IGuestConnectionListener)).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddBazaarVmGuestOpener();

        await using var provider = services.BuildServiceProvider();
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is GuestChannelOpener);
    }

    [Fact]
    public async Task With_targets_and_a_listener_the_opener_is_registered()
    {
        var services = Base();
        services.AddSingleton<IGuestOpenTargets, NoTargets>();
        services.AddBazaarVmGuestOpener();
        services.AddBazaarVmGuestOpener();

        await using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IHostedService>(), s => s is GuestChannelOpener);
    }
}
