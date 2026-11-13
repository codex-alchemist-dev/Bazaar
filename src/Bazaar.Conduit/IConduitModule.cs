namespace Bazaar.Conduit;

/// <summary>
/// A transport module is what the operator enables and what a client sees. It wraps one provider (or none) and
/// publishes offers as they become true. Doc 39 section 1 and section 2 catalogue.
/// </summary>
public interface IConduitModule
{
    /// <summary>Stable id used in settings keys (<c>transport.&lt;id&gt;.enabled</c>) and offers.</summary>
    string Id { get; }

    /// <summary>Name shown in the setup wizard.</summary>
    string DisplayName { get; }

    /// <summary>Scope of the offers this module publishes.</summary>
    ConduitScope Scope { get; }

    /// <summary>Lower numbers are tried first by clients. Doc 39 catalogue.</summary>
    int Priority { get; }

    /// <summary>True only for modules that are on by default (the LAN module).</summary>
    bool DefaultEnabled { get; }

    /// <summary>Starts the module. A failure is reported through <see cref="Status"/>, not thrown.</summary>
    ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken);

    /// <summary>Withdraws offers and releases everything the module holds, within a bounded time.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Current status, including the offers this module publishes now.</summary>
    ModuleStatus Status { get; }

    /// <summary>Raised when the offers change. The registry republishes them.</summary>
    event EventHandler<ModuleStatus>? StatusChanged;
}
