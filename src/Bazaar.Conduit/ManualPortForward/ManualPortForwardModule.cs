using System.Net;
using System.Net.Sockets;

namespace Bazaar.Conduit;

/// <summary>
/// Manual port forward (doc 39 section 2, priority 20). The operator enters a public host and port that the router or
/// static address maps to this server. An optional probe URL checks the mapping from outside. The module opens no port
/// itself, starts no process and holds no credentials.
/// </summary>
public sealed class ManualPortForwardModule : IConduitModule
{
    /// <summary>Settings key for the public host name or IP address (bare, no scheme, port or path).</summary>
    public const string PublicHostKey = "conduit.port-forward.public_host";

    /// <summary>Settings key for the public port. Defaults to the server's local port.</summary>
    public const string PublicPortKey = "conduit.port-forward.public_port";

    /// <summary>Settings key for the optional https probe URL. Empty means the offer is published unverified.</summary>
    public const string ProbeUrlKey = "conduit.port-forward.probe_url";

    private static readonly HttpClient _sharedProbeClient = new();

    private readonly IOutsideProbe _probe;
    private ModuleStatus _status;

    /// <summary>Creates the module with the HTTP outside probe.</summary>
    public ManualPortForwardModule()
        : this(new HttpOutsideProbe(_sharedProbeClient))
    {
    }

    /// <summary>Creates the module with a probe implementation, for tests or alternative probes.</summary>
    public ManualPortForwardModule(IOutsideProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
    }

    /// <inheritdoc />
    public string Id => "port-forward";

    /// <inheritdoc />
    public string DisplayName => "Manual port forward";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Internet;

    /// <inheritdoc />
    public int Priority => 20;

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

        var host = context.Settings.Read(PublicHostKey, string.Empty);
        if (host.Length == 0)
        {
            Fail("not-configured", context);
            return;
        }

        if (!IsValidHost(host))
        {
            Fail("invalid-public-host", context);
            return;
        }

        var port = context.Settings.Read(PublicPortKey, context.LocalPort);
        if (port is < 1 or > 65535)
        {
            Fail("invalid-public-port", context);
            return;
        }

        var probeText = context.Settings.Read(ProbeUrlKey, string.Empty);
        Uri? probe = null;
        if (probeText.Length > 0
            && (!Uri.TryCreate(probeText, UriKind.Absolute, out probe) || probe.Scheme != Uri.UriSchemeHttps))
        {
            Fail("invalid-probe-url", context);
            return;
        }

        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var uri = new Uri($"{Uri.UriSchemeWss}://{authority}:{port}/ws", UriKind.Absolute);

        SetStatus(new ModuleStatus(Id, ConduitModuleState.Starting, null, []));

        var verified = false;
        string? reason = null;
        if (probe is not null)
        {
            verified = await RunProbeAsync(probe, context, cancellationToken).ConfigureAwait(false);
            if (!verified)
            {
                reason = "probe-failed";
                ManualPortForwardLog.NotVerified(context.Logger, reason);
            }
        }

        var offer = new ConduitOffer(Id, Scope, uri, context.ServerKeyHash, null, false, verified);
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Running, reason, [offer]));
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Disabled, null, []));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts a bare DNS name, an IPv4 address or an IPv6 address. Anything with a scheme, port, path, query,
    /// user info, brackets or whitespace is rejected.
    /// </summary>
    internal static bool IsValidHost(string host)
    {
        if (host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '?' or '#' or '@' or '[' or ']'))
        {
            return false;
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }

    private async Task<bool> RunProbeAsync(Uri probe, ConduitContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ProbeAsync(probe, context.ServerKeyHash, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only the exception type is logged: exception messages can echo the probe URL.
            ManualPortForwardLog.NotVerified(context.Logger, ex.GetType().Name);
            return false;
        }
    }

    private void Fail(string reason, ConduitContext context)
    {
        ManualPortForwardLog.SettingsRejected(context.Logger, reason);
        SetStatus(new ModuleStatus(Id, ConduitModuleState.Failed, reason, []));
    }

    private void SetStatus(ModuleStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
