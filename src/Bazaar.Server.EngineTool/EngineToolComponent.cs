using Bazaar.Contracts.Gateway;
using Bazaar.Core;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.EngineTool;

/// <summary>
/// Offers <c>engine.call</c> to agents (doc 20; PROPOSED). Policy: exec class, actors only, autonomous with a notice, 120 calls an
/// hour, and the result hard-taints the session because it is content from a place the owner may have got from anywhere. The
/// dispatcher's own grant, taint and allowlist checks still run on every call.
/// </summary>
public sealed class EngineToolComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "engine-tool",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["engine-relay"],
        Provides: ["feature:engine-tool"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ToolCapability(
            EngineCallTool.ToolName, ToolTier.AutonomousWithNotice, CapabilityClass.Exec, SessionRoles.Actor,
            new RateRule(120, TimeSpan.FromHours(1)), ToolResultTaint.Hard));
        services.AddSingleton(Definition);
        services.AddSingleton(sp => new ToolRegistration(EngineCallTool.ToolName, new EngineCallTool(sp.GetRequiredService<EngineRelayBroker>())));
    }

    /// <summary>What the model is told about the tool.</summary>
    public static ModelToolDefinition Definition { get; } = new(
        EngineCallTool.ToolName,
        "Runs one game-engine tool call (for example Roblox Studio or Godot) for you through the owner's connected desktop. " +
        "Reads need a grant too; write and exec calls are also checked for approval and are recorded in the chat. The call fails " +
        "if no desktop is connected or the grant does not cover the tool.",
        """
        {
          "type": "object",
          "properties": {
            "adapter_id": { "type": "string", "description": "The engine adapter, for example roblox." },
            "studio_id": { "type": "string", "description": "The target project or Studio." },
            "tool": { "type": "string", "description": "The engine tool name." },
            "grant_id": { "type": "string", "description": "The grant that covers this tool." },
            "args": { "type": "object", "description": "The tool arguments." }
          },
          "required": ["adapter_id", "studio_id", "tool"],
          "additionalProperties": false
        }
        """);
}
