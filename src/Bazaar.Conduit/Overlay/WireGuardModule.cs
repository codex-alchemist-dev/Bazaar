using System.Net;
using System.Net.Sockets;

namespace Bazaar.Conduit;

/// <summary>
/// Operator-run WireGuard interface (doc 39 section 2, priority 41). Bazaar does not manage the interface. The operator
/// enters the host's WireGuard address in <see cref="AddressKey"/>, and the module publishes one <c>wss</c> offer on it.
/// </summary>
public sealed class WireGuardModule : IConduitModule
{
    /// <summary>Settings key for the host's WireGuard address, an IPv4 or IPv6 literal.</summary>
    public const string AddressKey = "conduit.overlay-wireguard.address";

    /// <summary>Settings key for the port the WireGuard address listens on. Defaults to the server's local port.</summary>
    public const string PortKey = "conduit.overlay-wireguard.port";

    private ModuleStatus _status;

    /// <summary>Creates the module. It reads its address from settings and holds no process or interface.</summary>
    public WireGuardModule()
    {
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
    }

    /// <inheritdoc />
    public string Id => "overlay-wireguard";

    /// <inheritdoc />
    public string DisplayName => "WireGuard overlay";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Overlay;

    /// <inheritdoc />
    public int Priority => 41;

    /// <inheritdoc />
    public bool DefaultEnabled => false;

    /// <inheritdoc />
    public ModuleStatus Status => _status;

    /// <inheritdoc />
    public event EventHandler<ModuleStatus>? StatusChanged;

    /// <inheritdoc />
    public ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var port = context.Settings.Read(PortKey, context.LocalPort);
        if (!OverlayOffers.IsValidPort(port))
        {
            Fail("invalid-port", context);
            return ValueTask.CompletedTask;
        }

        var text = context.Settings.Read(AddressKey, string.Empty).Trim();
        if (text.Length == 0)
        {
            Fail("not-configured", context);
            return ValueTask.CompletedTask;
        }

        if (!TryParseLiteral(text, out var address))
        {
            Fail("invalid-address", context);
            return ValueTask.CompletedTask;
        }

        var offer = OverlayOffers.Create(Id, address, port, context.ServerKeyHash);
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Running, null, [offer]));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Disabled, null, []));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts an IPv4 literal in canonical dotted form or an IPv6 literal without a zone (scope) id. Host names,
    /// brackets, ports and legacy IPv4 forms such as "1.2.3" are rejected.
    /// </summary>
    internal static bool TryParseLiteral(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Any(c => char.IsWhiteSpace(c) || c is '[' or ']' or '%' or '/' or '\\' or '?' or '#' or '@'))
        {
            return false;
        }

        if (!IPAddress.TryParse(text, out var parsed))
        {
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6 || parsed.ScopeId != 0)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private void Fail(string reason, ConduitContext context)
    {
        OverlayLog.DidNotStart(context.Logger, Id, reason);
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Failed, reason, []));
    }

    private void SetStatus(ModuleStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
