using Bazaar.Contracts.Gateway;
using Bazaar.Core;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.AgentTools;

/// <summary>
/// The <c>screen.capture</c> and <c>screen.input</c> agent tools as a Bazaar Component (docs/design/59, section 4). Both are soft
/// dependencies: the vm-display module supplies <see cref="IGuestDisplay"/> and the host supplies <see cref="IAgentDisplayTargets"/>.
/// When either is not registered when this component runs, no tool is registered and agents are simply not offered screen access.
/// The turn path collects every <see cref="ToolRegistration"/> and <see cref="ModelToolDefinition"/>, so nothing else wires them.
/// Policy entries (docs/design/15) belong to the host, which owns the policy types; until it adds them the policy engine fails closed.
/// </summary>
public sealed class VmDisplayAgentToolsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-agent-tools",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:vm-display-agent-tools"],
        // Soft: vm-display provides the display service and the host (for example vm-display-agent-targets) the agent display targets. Without
        // them this component offers nothing and the rest of the server is unaffected. Soft edges never set start order, so a provider of
        // IAgentDisplayTargets must register before this component runs; the host's composition test pins that order.
        SoftDependsOn: ["vm-display", "vm-display-agent-targets"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind first so a bad setting fails at start even when the tools are not offered.
        ScreenToolsOptions options = ScreenToolsOptions.Bind(configuration);

        if (!services.Any(d => d.ServiceType == typeof(IGuestDisplay)) ||
            !services.Any(d => d.ServiceType == typeof(IAgentDisplayTargets)))
        {
            return;
        }

        services.AddSingleton(options);
        services.AddSingleton(sp => new ScreenCaptureTool(
            sp.GetRequiredService<IGuestDisplay>(),
            sp.GetRequiredService<IAgentDisplayTargets>(),
            sp.GetRequiredService<ScreenToolsOptions>()));
        services.AddSingleton(sp => new ScreenInputTool(
            sp.GetRequiredService<IGuestDisplay>(),
            sp.GetRequiredService<IAgentDisplayTargets>(),
            sp.GetRequiredService<ScreenToolsOptions>(),
            // A native size port the host registered wins; otherwise the display module's reader is used when it is there.
            sp.GetService<IDisplayNativeSize>() ?? (sp.GetService<IDisplayNativeSizeReader>() is { } reader ? new ReaderNativeSize(reader) : null)));

        services.AddSingleton(sp => new ToolRegistration(ScreenCaptureTool.ToolName, sp.GetRequiredService<ScreenCaptureTool>()));
        services.AddSingleton(sp => new ToolRegistration(ScreenInputTool.ToolName, sp.GetRequiredService<ScreenInputTool>()));
        services.AddSingleton(ScreenCaptureTool.Definition);
        services.AddSingleton(ScreenInputTool.Definition);
    }
}
