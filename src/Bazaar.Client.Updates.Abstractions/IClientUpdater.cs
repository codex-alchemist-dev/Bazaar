namespace Bazaar.Client.Updates;

/// <summary>
/// The client's self-updater (doc 28, Client updater). The cycle is check, download, apply, and nothing installs
/// until the package has passed verification. Calls are serialised: a second call waits for the first.
/// </summary>
public interface IClientUpdater
{
    /// <summary>The channel the next check uses.</summary>
    UpdateChannel Channel { get; }

    /// <summary>The latest snapshot.</summary>
    ClientUpdateState State { get; }

    /// <summary>Switches channel and resets the cycle to idle. The caller records the switch as an audited decision (doc 49).</summary>
    void SetChannel(UpdateChannel channel);

    /// <summary>Asks the channel's feed for a newer release. Never throws for feed problems: they land in <see cref="State"/>.</summary>
    Task<ClientUpdateState> CheckAsync(CancellationToken ct);

    /// <summary>Downloads and verifies the offered release. Only valid in <see cref="ClientUpdatePhase.Available"/>.</summary>
    Task<ClientUpdateState> DownloadAsync(IProgress<int>? progress, CancellationToken ct);

    /// <summary>Restarts into the verified release. Only valid in <see cref="ClientUpdatePhase.Ready"/>. On success the process exits and restarts.</summary>
    Task<ClientUpdateState> ApplyAsync(CancellationToken ct);
}
