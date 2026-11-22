using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Display;

/// <summary>
/// Policy entries for the agent screen tools (doc 15, "Screen tools" [PROPOSED]; doc 59, sections 4 and 6). The tools themselves are
/// registered by the vm-display-agent-tools component; the host owns the policy types, and the policy engine denies a tool it has no
/// <see cref="ToolCapability"/> for, so without these entries every screen call is refused as an unknown tool.
/// </summary>
public static class ScreenToolPolicyExtensions
{
    /// <summary>Name of the capture tool (matches <c>ScreenCaptureTool.ToolName</c>).</summary>
    public const string CaptureToolName = "screen.capture";

    /// <summary>Name of the input tool (matches <c>ScreenInputTool.ToolName</c>).</summary>
    public const string InputToolName = "screen.input";

    /// <summary>
    /// <c>screen.capture</c>: Tier 0, read class, open to reader and actor sessions, and its result always hard-taints the session (a
    /// frame is external content; not a setting). <c>screen.input</c>: Tier 1 (a GC notice; hard taint escalates it to Tier 2), exec class,
    /// actor sessions only. The finer rules (account browsers under hard taint, kill switch, leased credentials in typed text) are the
    /// display gate's and are not repeated here.
    /// </summary>
    public static IReadOnlyList<ToolCapability> Capabilities { get; } =
    [
        new(CaptureToolName, ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader | SessionRoles.Actor, ResultTaint: ToolResultTaint.Hard),
        new(InputToolName, ToolTier.AutonomousWithNotice, CapabilityClass.Exec, SessionRoles.Actor),
    ];

    /// <summary>Registers the screen tools' <see cref="ToolCapability"/> entries. Safe to call when the tools are not offered.</summary>
    public static IServiceCollection AddBazaarScreenToolPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var capability in Capabilities)
        {
            if (!services.Any(d => d.ServiceType == typeof(ToolCapability) && d.ImplementationInstance is ToolCapability existing && existing.Name == capability.Name))
            {
                services.AddSingleton(capability);
            }
        }

        return services;
    }
}
