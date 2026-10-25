namespace Bazaar.Server.Providers.Fakes;

/// <summary>In-memory VM provider for tests. Reports a fixed usability only.</summary>
public sealed class FakeVmProvider : IVmProvider
{
    private readonly bool _usable;
    private readonly string? _reason;

    /// <summary>Creates a fake VM provider with the given kind, usability and failure reason.</summary>
    public FakeVmProvider(string kind = "fake_vm", bool usable = true, string? reason = null)
    {
        Kind = kind;
        _usable = usable;
        _reason = reason;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public ProviderStatus Probe() =>
        new(Kind, _usable, Array.Empty<string>(), _usable ? null : _reason ?? "fake provider unavailable");
}
