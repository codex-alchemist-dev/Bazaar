using Bazaar.Server.ModuleHost;

namespace Bazaar.ModulePermissions.HostServices;

/// <summary>
/// Puts the permission gate in front of the host services a module is handed (doc 48 section 1.1). It wraps the factory of the
/// real services: every call a module makes first asks <see cref="IModulePermissionGate.Demand"/> for one exact access, under the
/// module id this factory was asked for, and reaches the real service only when the gate allows it. Nothing is cached, so a
/// service handed out earlier is refused as soon as the module is deactivated or its set no longer covers the call.
/// </summary>
/// <remarks>
/// The real services are made per module by <paramref name="inner"/>. They must keep one module's panes, jobs and posts apart
/// from another's: the gate decides whether a module may use a name, not who owns the record behind it.
/// </remarks>
public sealed class GuardedModuleHostServicesFactory(IModuleHostServicesFactory inner, IModulePermissionGate gate) : IModuleHostServicesFactory
{
    private readonly IModuleHostServicesFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IModulePermissionGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <inheritdoc />
    public IModuleHostServices CreateFor(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        var real = _inner.CreateFor(moduleId) ?? throw new InvalidOperationException("The host service factory returned no services.");
        if (!string.Equals(real.ModuleId, moduleId, StringComparison.Ordinal))
        {
            // Services made for another module would run under this module's permissions but act as the other one.
            throw new InvalidOperationException("The host service factory returned services made for another module.");
        }

        return new GuardedModuleHostServices(moduleId, real, _gate);
    }
}
