using System.Collections.Immutable;

namespace Bazaar.Server.Security.Policy;

/// <summary>
/// The fixed set of tools the policy engine knows about. Built once at startup and never changed while running.
/// A tool that is not registered is denied (fail closed). Registration checks the fixed floors from doc 15, so a
/// misregistered tool fails at startup rather than at dispatch.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly ImmutableDictionary<string, ToolCapability> _tools;

    private CapabilityRegistry(ImmutableDictionary<string, ToolCapability> tools) => _tools = tools;

    /// <summary>Every registered tool, ordered by name.</summary>
    public IEnumerable<ToolCapability> Tools => _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>Builds a registry. Throws if a name repeats, a name is blank, or a floor is broken.</summary>
    /// <exception cref="ArgumentException">A tool breaks a registration rule.</exception>
    public static CapabilityRegistry Create(IEnumerable<ToolCapability> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var builder = ImmutableDictionary.CreateBuilder<string, ToolCapability>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            Validate(tool);
            if (!builder.TryAdd(tool.Name, tool))
            {
                throw new ArgumentException($"Tool '{tool.Name}' is registered twice.", nameof(tools));
            }
        }

        return new CapabilityRegistry(builder.ToImmutable());
    }

    /// <summary>Looks up a tool by exact name.</summary>
    public bool TryGet(string tool, out ToolCapability capability)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return _tools.TryGetValue(tool, out capability!);
    }

    private static void Validate(ToolCapability tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name != tool.Name.Trim())
        {
            throw new ArgumentException("Tool names must be non-blank and must not have surrounding whitespace.", nameof(tool));
        }

        if (!Enum.IsDefined(tool.Tier) || !Enum.IsDefined(tool.Class))
        {
            throw new ArgumentException($"Tool '{tool.Name}' has an undefined tier or class.", nameof(tool));
        }

        if (tool.Roles == SessionRoles.None || (tool.Roles & ~(SessionRoles.Reader | SessionRoles.Actor)) != 0)
        {
            throw new ArgumentException($"Tool '{tool.Name}' must name at least one known session role.", nameof(tool));
        }

        // Floor: merges are always Tier 2 or stricter (doc 15: "any merge" is Tier 2; the setting may not loosen it).
        if (tool.Class == CapabilityClass.Merge && tool.Tier < ToolTier.OwnerApproves)
        {
            throw new ArgumentException($"Tool '{tool.Name}' merges, so it cannot be below Tier 2.", nameof(tool));
        }

        // Reader sessions hold no outward or write tools (doc 15). Only read-class tools may run there.
        if ((tool.Roles & SessionRoles.Reader) != 0 && tool.Class != CapabilityClass.Read)
        {
            throw new ArgumentException($"Tool '{tool.Name}' is open to reader sessions but is not a read tool.", nameof(tool));
        }
    }
}
