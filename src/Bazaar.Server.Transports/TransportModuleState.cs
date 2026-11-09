namespace Bazaar.Server.Transports;

/// <summary>Lifecycle state of one transport module, shown in the admin panel.</summary>
public enum TransportModuleState
{
    /// <summary>Not enabled by the operator.</summary>
    Disabled,

    /// <summary>Started but no offer yet.</summary>
    Starting,

    /// <summary>Offering at least one address.</summary>
    Running,

    /// <summary>Was running; a dependency failed but the module still tries to recover.</summary>
    Degraded,

    /// <summary>Stopped on error. The reason is in <see cref="ModuleStatus.Reason"/>.</summary>
    Failed,
}
