namespace Bazaar.Wire.Abstractions;

/// <summary>
/// The <c>wire.transports.enabled</c> setting (doc 90 section 8). Unset means every installed transport except the
/// ones that ship off by default (<c>grpc-web</c> and <c>webtransport</c>, owner card 2026-11-19); an operator turns
/// those on by naming them. An explicit list enables exactly the names in it.
/// </summary>
public sealed class WireTransportsEnabledOptions
{
    /// <summary>The settings key this class reads.</summary>
    public const string SettingKey = "wire.transports.enabled";

    private static readonly string[] _offByDefaultNames = [WireNames.GrpcWeb, WireNames.WebTransport];

    /// <summary>Transports that are off until an operator lists them.</summary>
    public static IReadOnlyList<string> OffByDefault => _offByDefaultNames;

    /// <summary>The operator's list, or null when the setting is unset.</summary>
    public IReadOnlyCollection<string>? Enabled { get; set; }

    /// <summary>True when <paramref name="transport"/> may accept connections.</summary>
    public bool IsEnabled(string transport)
    {
        ArgumentException.ThrowIfNullOrEmpty(transport);
        if (Enabled is null) return !_offByDefaultNames.Contains(transport, StringComparer.Ordinal);
        return Enabled.Contains(transport, StringComparer.Ordinal);
    }
}
