using System.Net;

namespace Bazaar.Conduit;

/// <summary>
/// Tailscale or Headscale overlay (doc 39 section 2, priority 40). Publishes one <c>wss</c> offer on the host's overlay
/// address. Bazaar does not run or configure Tailscale; the module only reads the address it reports. The offer keeps
/// the server key pin, and the overlay control plane is the operator's trade-off (doc 39 section 2).
/// </summary>
public sealed class TailscaleModule : IConduitModule
{
    /// <summary>Settings key for the port the overlay address listens on. Defaults to the server's local port.</summary>
    public const string PortKey = "conduit.overlay-tailscale.port";

    private readonly IOverlayAddressSource _source;
    private ModuleStatus _status;

    /// <summary>Creates the module with the Tailscale CLI address source.</summary>
    public TailscaleModule()
        : this(new TailscaleAddressSource())
    {
    }

    /// <summary>Creates the module with an address source, for tests or alternative sources.</summary>
    public TailscaleModule(IOverlayAddressSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
    }

    /// <inheritdoc />
    public string Id => "overlay-tailscale";

    /// <inheritdoc />
    public string DisplayName => "Tailscale overlay";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Overlay;

    /// <inheritdoc />
    public int Priority => 40;

    /// <inheritdoc />
    public bool DefaultEnabled => false;

    /// <inheritdoc />
    public ModuleStatus Status => _status;

    /// <inheritdoc />
    public event EventHandler<ModuleStatus>? StatusChanged;

    /// <inheritdoc />
    public async ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var port = context.Settings.Read(PortKey, context.LocalPort);
        if (!OverlayOffers.IsValidPort(port))
        {
            Fail("invalid-port", context);
            return;
        }

        SetStatus(new ModuleStatus(Id, ConduitModuleState.Starting, null, []));

        IPAddress? address;
        try
        {
            address = await _source.GetHostOverlayAddressAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the exception type is reported: messages can echo command output or paths.
            Fail(ex.GetType().Name, context);
            return;
        }

        if (address is null)
        {
            Fail("overlay-not-running", context);
            return;
        }

        var offer = OverlayOffers.Create(Id, address, port, context.ServerKeyHash);
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Running, null, [offer]));
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Disabled, null, []));
        return ValueTask.CompletedTask;
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
