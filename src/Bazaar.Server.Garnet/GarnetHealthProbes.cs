using System.Text;
using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet;

/// <summary>
/// The four health probes for Garnet (docs/design/42-deps-as-modules.md section 11, docs/design/40 section 4b.2). Each probe opens a client
/// for its own use and closes it, so a child that changed its port is picked up at the next probe. Probes report results and never throw
/// on a failed server call; caller cancellation is not swallowed. Reasons never carry the endpoint, the port or a secret. All probes are
/// non-critical (section 4b.3).
/// <para>
/// Garnet has no <c>maxmemory-policy</c> and reports less through INFO and CONFIG than Valkey, so the self-test is a round trip only, and
/// the dependencies probe measures memory against the configured <see cref="GarnetCacheOptions.MaxMemory"/> for a managed server. When
/// the server reports no memory figure the probe passes (unknown is not a failure).
/// </para>
/// </summary>
public sealed class GarnetHealthProbes : IValkeyHealthProbes
{
    private const string _readinessKey = "bazaar:health:readiness";
    private const string _selfTestKey = "bazaar:health:selftest";
    private static readonly TimeSpan _probeTtl = TimeSpan.FromSeconds(30);

    private readonly IValkeyChildStatus _child;
    private readonly IValkeyConnectionSource _connection;
    private readonly IValkeyClientFactory _clientFactory;
    private readonly GarnetCacheOptions _options;

    /// <summary>Creates the probes over the child's status, the connection source and the client factory.</summary>
    public GarnetHealthProbes(
        IValkeyChildStatus child,
        IValkeyConnectionSource connection,
        IValkeyClientFactory clientFactory,
        GarnetCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(options);
        _child = child;
        _connection = connection;
        _clientFactory = clientFactory;
        _options = options;
    }

    /// <inheritdoc />
    public Task<ValkeyProbeResult> LivenessAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.Liveness, _ => Task.FromResult(LivenessReason()), ct);

    /// <inheritdoc />
    public Task<ValkeyProbeResult> ReadinessAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.Readiness, ReadinessReasonAsync, ct);

    /// <inheritdoc />
    public Task<ValkeyProbeResult> DependenciesAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.Dependencies, DependenciesReasonAsync, ct);

    /// <inheritdoc />
    public Task<ValkeyProbeResult> SelfTestAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.SelfTest, SelfTestReasonAsync, ct);

    /// <summary>Null when the child is running or the server is external (nothing to supervise).</summary>
    private string? LivenessReason() => _child.State switch
    {
        ValkeyChildState.Disabled or ValkeyChildState.Running => null,
        _ => $"managed child is {_child.State}",
    };

    private async Task<string?> ReadinessReasonAsync(CancellationToken ct)
    {
        await using var client = await ConnectAsync(ct);
        await client.PingAsync(ct);
        var expected = Encoding.UTF8.GetBytes("ok");
        await client.SetAsync(_readinessKey, expected, _probeTtl, ct);
        var actual = await client.GetAsync(_readinessKey, ct);
        return actual is not null && actual.AsSpan().SequenceEqual(expected) ? null : "round trip on the readiness key did not match";
    }

    private async Task<string?> DependenciesReasonAsync(CancellationToken ct)
    {
        await using var client = await ConnectAsync(ct);
        var memory = await client.MemoryInfoAsync(ct);
        var maxBytes = memory.MaxBytes;
        if (maxBytes <= 0 && _options.Managed && GarnetSize.TryParseBytes(_options.MaxMemory, out var configured))
        {
            maxBytes = configured;
        }

        if (maxBytes <= 0 || memory.UsedBytes <= 0)
        {
            return null;
        }

        var percent = memory.UsedBytes * 100.0 / maxBytes;
        return percent >= _options.MemoryWarnPercent
            ? $"memory at {percent:0}% of the limit (warns at {_options.MemoryWarnPercent}%)"
            : null;
    }

    private async Task<string?> SelfTestReasonAsync(CancellationToken ct)
    {
        await using var client = await ConnectAsync(ct);
        var expected = Encoding.UTF8.GetBytes("self-test");
        await client.SetAsync(_selfTestKey, expected, _probeTtl, ct);
        var actual = await client.GetAsync(_selfTestKey, ct);
        await client.DeleteAsync(_selfTestKey, ct);
        if (actual is null || !actual.AsSpan().SequenceEqual(expected))
        {
            return "round trip on the self-test key did not match";
        }

        return null;
    }

    private async Task<IValkeyClient> ConnectAsync(CancellationToken ct) =>
        _clientFactory.Create(await _connection.GetConnectionStringAsync(ct));

    private static async Task<ValkeyProbeResult> RunAsync(
        ValkeyProbeKind kind,
        Func<CancellationToken, Task<string?>> check,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var reason = await check(ct);
            return new ValkeyProbeResult(kind, reason is null, Critical: false, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type name only: connection exception messages can echo the endpoint.
            return new ValkeyProbeResult(kind, false, Critical: false, $"probe failed ({ex.GetType().Name})");
        }
    }
}
