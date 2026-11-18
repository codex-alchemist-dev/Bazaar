using System.Text;

namespace Bazaar.Server.Valkey;

/// <summary>
/// The four Kodo health probes for Valkey (docs/design/42-deps-as-modules.md section 11). Each probe reports a result and never throws, except
/// that cancellation of the caller's token is not swallowed. Reasons never carry the endpoint or a secret.
/// <para>
/// The self-test covers the round trip only. The eviction check in doc 42 is not implemented: which eviction behaviour to expect
/// is still open (see the report on the Valkey module).
/// </para>
/// </summary>
public sealed class ValkeyHealthProbes : IValkeyHealthProbes
{
    private const string _readinessKey = "bazaar:health:readiness";
    private const string _selfTestKey = "bazaar:health:selftest";
    private static readonly TimeSpan _probeTtl = TimeSpan.FromSeconds(30);

    private readonly IValkeyChildStatus _child;
    private readonly IValkeyClient _client;
    private readonly ValkeyCacheOptions _options;

    /// <summary>Creates the probes over one client and the managed child's status.</summary>
    public ValkeyHealthProbes(IValkeyChildStatus child, IValkeyClient client, ValkeyCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _child = child;
        _client = client;
        _options = options;
    }

    /// <inheritdoc />
    public Task<ValkeyProbeResult> LivenessAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.Liveness, critical: false, _ => Task.FromResult(LivenessReason()), ct);

    /// <inheritdoc />
    public Task<ValkeyProbeResult> ReadinessAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.Readiness, critical: true, ReadinessReasonAsync, ct);

    /// <inheritdoc />
    public Task<ValkeyProbeResult> DependenciesAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.Dependencies, critical: false, DependenciesReasonAsync, ct);

    /// <inheritdoc />
    public Task<ValkeyProbeResult> SelfTestAsync(CancellationToken ct) =>
        RunAsync(ValkeyProbeKind.SelfTest, critical: false, SelfTestReasonAsync, ct);

    /// <summary>Null when the child is running or the server is external (nothing to supervise).</summary>
    private string? LivenessReason() => _child.State switch
    {
        ValkeyChildState.Disabled or ValkeyChildState.Running => null,
        _ => $"managed child is {_child.State}",
    };

    private async Task<string?> ReadinessReasonAsync(CancellationToken ct)
    {
        await _client.PingAsync(ct);
        var expected = Encoding.UTF8.GetBytes("ok");
        await _client.SetAsync(_readinessKey, expected, _probeTtl, ct);
        var actual = await _client.GetAsync(_readinessKey, ct);
        return actual is not null && actual.AsSpan().SequenceEqual(expected) ? null : "round trip on the readiness key did not match";
    }

    private async Task<string?> DependenciesReasonAsync(CancellationToken ct)
    {
        var memory = await _client.MemoryInfoAsync(ct);
        if (memory.MaxBytes <= 0)
        {
            return null;
        }

        var percent = memory.UsedBytes * 100.0 / memory.MaxBytes;
        return percent >= _options.MemoryWarnPercent
            ? $"memory at {percent:0}% of the limit (warns at {_options.MemoryWarnPercent}%)"
            : null;
    }

    private async Task<string?> SelfTestReasonAsync(CancellationToken ct)
    {
        var expected = Encoding.UTF8.GetBytes("self-test");
        await _client.SetAsync(_selfTestKey, expected, _probeTtl, ct);
        var actual = await _client.GetAsync(_selfTestKey, ct);
        await _client.DeleteAsync(_selfTestKey, ct);
        if (actual is null || !actual.AsSpan().SequenceEqual(expected))
        {
            return "round trip on the self-test key did not match";
        }

        return await _client.GetAsync(_selfTestKey, ct) is null ? null : "self-test key still present after delete";
    }

    private static async Task<ValkeyProbeResult> RunAsync(
        ValkeyProbeKind kind,
        bool critical,
        Func<CancellationToken, Task<string?>> check,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var reason = await check(ct);
            return new ValkeyProbeResult(kind, reason is null, critical, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type name only: connection exception messages can echo the endpoint.
            return new ValkeyProbeResult(kind, false, critical, $"probe failed ({ex.GetType().Name})");
        }
    }
}
