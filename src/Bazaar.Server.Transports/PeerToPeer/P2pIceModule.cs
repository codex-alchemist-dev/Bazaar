namespace Bazaar.Server.Transports.PeerToPeer;

/// <summary>
/// Peer-to-peer transport module, STUN stage only (doc 39 section 2, row <c>p2p-ice</c>). It gathers one
/// server-reflexive candidate and stops there: candidates cannot be published until a signaling path exists (doc 39
/// work package 5), so the module reports <c>Starting</c> with <c>signaling-not-configured</c> and offers nothing.
/// It never reports a data path as running.
/// </summary>
public sealed class P2pIceModule : ITransportModule
{
    /// <summary>Module id, used in the <c>transport.p2p-ice.*</c> settings keys.</summary>
    public const string ModuleId = "p2p-ice";

    /// <summary>Settings key for the operator's STUN server list ("host:port" strings).</summary>
    public const string StunServersKey = "transport.p2p-ice.stun_servers";

    /// <summary>Reason shown while the module waits for a signaling path.</summary>
    public const string SignalingNotConfiguredReason = "signaling-not-configured";

    /// <summary>Reason shown when the settings list no STUN server.</summary>
    public const string NoStunServersReason = "no-stun-servers";

    private readonly StunGatherer _gatherer;

    /// <summary>Creates the module over a gatherer. The default uses real UDP sockets and system DNS.</summary>
    public P2pIceModule(StunGatherer? gatherer = null)
    {
        _gatherer = gatherer ?? new StunGatherer(new UdpStunTransport());
    }

    /// <inheritdoc />
    public string Id => ModuleId;

    /// <inheritdoc />
    public string DisplayName => "Peer-to-peer (ICE, STUN stage)";

    /// <inheritdoc />
    public TransportScope Scope => TransportScope.PeerToPeer;

    /// <inheritdoc />
    public int Priority => 55;

    /// <inheritdoc />
    public bool DefaultEnabled => false;

    /// <inheritdoc />
    public ModuleStatus Status { get; private set; } = new(ModuleId, TransportModuleState.Disabled, null, []);

    /// <summary>The reflexive candidate from the last successful gathering, or null.</summary>
    public StunReflexiveCandidate? LastReflexive { get; private set; }

    /// <inheritdoc />
    public event EventHandler<ModuleStatus>? StatusChanged;

    /// <inheritdoc />
    public async ValueTask StartAsync(ReachabilityContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var servers = ReadServers(context.Settings);
        if (servers.Count == 0)
        {
            Publish(TransportModuleState.Failed, NoStunServersReason);
            return;
        }

        Publish(TransportModuleState.Starting, null);
        var result = await _gatherer.GatherAsync(servers, context.Time, cancellationToken).ConfigureAwait(false);
        if (result.Candidate is null)
        {
            LastReflexive = null;
            Publish(TransportModuleState.Failed, result.Reason ?? StunGatherer.NoStunResponseReason);
            return;
        }

        LastReflexive = result.Candidate;
        Publish(TransportModuleState.Starting, SignalingNotConfiguredReason);
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        LastReflexive = null;
        Publish(TransportModuleState.Disabled, null);
        return ValueTask.CompletedTask;
    }

    private static List<string> ReadServers(ITransportSettings settings)
    {
        var configured = settings.Read<IReadOnlyList<string>?>(StunServersKey, null);
        if (configured is null)
        {
            configured = settings.Read<string[]?>(StunServersKey, null);
        }

        if (configured is null)
        {
            return [];
        }

        return configured
            .Where(server => !string.IsNullOrWhiteSpace(server))
            .Select(server => server.Trim())
            .ToList();
    }

    private void Publish(TransportModuleState state, string? reason)
    {
        Status = new ModuleStatus(ModuleId, state, reason, []);
        StatusChanged?.Invoke(this, Status);
    }
}
