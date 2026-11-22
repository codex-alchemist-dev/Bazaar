using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Admin.Placeholders;

/// <summary>
/// PLACEHOLDER GATE. Closed: this build has no durable audit store, so apply never runs. The placeholder lock and audit log below
/// are registered only through this gate, and the gate is never opened in this build. The Host adapter replaces this gate.
/// </summary>
internal sealed class PlaceholderApplyGate : ICliApplyGate
{
    public bool DurableAuditStoreAvailable => false;

    public void RegisterApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISetupInstanceLock>(new NullSetupInstanceLock());
        services.AddSingleton<IAuditLog>(new NullSetupAuditLog());
        services.AddSingleton<ISettingAuditSink>(new InMemorySettingAuditSink());
    }
}
