using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Transports;

/// <summary>Result of a side-effect-free probe of a reachability path.</summary>
/// <param name="Usable">True when the path looks usable from this host.</param>
/// <param name="Reason">Short machine-readable reason, for example <c>no-direct-path</c>.</param>
/// <param name="Candidates">Addresses the path would publish.</param>
public sealed record ReachabilityResult(bool Usable, string Reason, IReadOnlyList<Uri> Candidates);

/// <summary>Read access to a module's settings. Implemented over the Settings table (doc 03).</summary>
public interface ITransportSettings
{
    /// <summary>Returns the value for <paramref name="key"/> or <paramref name="defaultValue"/> when unset.</summary>
    T Read<T>(string key, T defaultValue);
}

/// <summary>Everything a module needs from the host. Doc 39 section 1.</summary>
/// <param name="LocalPort">The server's own listener port on the host.</param>
/// <param name="ServerKeyHash">Pinned server key hash, for offers.</param>
/// <param name="Settings">Module settings (<c>transport.*</c> keys).</param>
/// <param name="Time">Injected clock (the testing clock in tests).</param>
/// <param name="Logger">Logger. Must never log tokens, tunnel credentials or mapping secrets.</param>
public sealed record ReachabilityContext(
    int LocalPort,
    string ServerKeyHash,
    ITransportSettings Settings,
    TimeProvider Time,
    ILogger Logger);

/// <summary>Current status of one module, as shown in the admin panel.</summary>
/// <param name="ModuleId">The module id.</param>
/// <param name="State">Lifecycle state.</param>
/// <param name="Reason">Human-readable reason when not running, otherwise null.</param>
/// <param name="Offers">Offers currently published.</param>
public sealed record ModuleStatus(string ModuleId, TransportModuleState State, string? Reason, IReadOnlyList<TransportOffer> Offers);
