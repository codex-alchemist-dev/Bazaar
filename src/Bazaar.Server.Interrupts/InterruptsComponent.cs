using Bazaar.Core;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Health;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup.FirstRun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

        // System producer (doc 24 section 3): one listener for the Health and Security seams, off until interrupt.system_targets says otherwise.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, InterruptSettingContribution>());
        services.TryAddSingleton<IInterruptGcResolver>(sp =>
            new InterruptGcResolver(sp.GetService<IPresenceStore>(), sp.GetService<IGeneralGcReader>()));
        services.TryAddSingleton(sp => new InterruptSystemProducer(
            sp.GetRequiredService<IInterruptStackService>(),
            sp.GetService<IInterruptSystemTargetsReader>(),
            sp.GetService<IAgentDirectoryReader>(),
            sp.GetService<IInterruptGcResolver>(),
            sp.GetService<ILogger<InterruptSystemProducer>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBuildBrokenListener, InterruptSystemProducer>(sp => sp.GetRequiredService<InterruptSystemProducer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITaintRaisedListener, InterruptSystemProducer>(sp => sp.GetRequiredService<InterruptSystemProducer>()));
    }
}
