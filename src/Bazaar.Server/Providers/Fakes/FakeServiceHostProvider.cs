namespace Bazaar.Server.Providers.Fakes;

/// <summary>In-memory service host provider for tests. Reports a fixed usability and a fixed recovery policy.</summary>
public sealed class FakeServiceHostProvider : IServiceHostProvider
{
    private static readonly string[] _restartActions = ["restart", "restart", "restart"];

    private readonly bool _usable;
    private readonly string? _reason;
    private readonly bool _configured;

    /// <summary>Creates a fake service host with the given kind, usability, failure reason and recovery policy.</summary>
    public FakeServiceHostProvider(string kind = "fake_service_host", bool usable = true, string? reason = null, bool configured = true)
    {
        Kind = kind;
        _usable = usable;
        _reason = reason;
        _configured = configured;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public ProviderStatus Probe() =>
        new(Kind, _usable, Array.Empty<string>(), _usable ? null : _reason ?? "fake provider unavailable");

    /// <inheritdoc />
    public Task<ServiceRecoveryReport> RecoveryAsync(CancellationToken ct) =>
        Task.FromResult(new ServiceRecoveryReport(_configured, _restartActions));
}
