using Bazaar.Core;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Setup.FirstRun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Interrupts;

/// <summary>
/// The Interrupt System module (docs/design/24 section 3). Registers the stack service, its read-only reader and the reason
/// cipher. Reasons are sealed through the GC body codec and system pushes resolve the company owner, so the component needs
/// <c>gc</c> and <c>setup</c>. The <c>interrupt_frame</c> migrations ship with the server's own SQL. Host composition maps
/// <c>POST /company/interrupts</c> with <see cref="InterruptsReadRoute.MapCompanyInterrupts"/>.
/// </summary>
public sealed class InterruptsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "interrupts",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc", "setup"],
        Provides: ["feature:interrupt-stack"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IInterruptReasonCipher>(sp => new GcBodyInterruptReasonCipher(sp.GetRequiredService<IGcBodyCodec>()));
        services.TryAddSingleton(sp => new InterruptStackService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IInterruptReasonCipher>(),
            sp.GetRequiredService<IFirstRunOwner>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IInterruptStackService>(sp => sp.GetRequiredService<InterruptStackService>());
        services.TryAddSingleton<IInterruptStackReader>(sp => sp.GetRequiredService<InterruptStackService>());
    }
}
