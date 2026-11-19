using Bazaar.Server.Agents.LoopGuard;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>
/// Thrown by <see cref="GcAgentPostSink"/> when a loop guard trips on an agent post. Nothing was appended to the GC when it is
/// thrown, so the turn runner ends the turn as refused and releases the turn's inbox items.
/// </summary>
public sealed class AgentPostRefusedException(GuardKind guardKind, string reason) : Exception(reason)
{
    /// <summary>The guard that tripped.</summary>
    public GuardKind GuardKind { get; } = guardKind;

    /// <summary>The guard's reason, as the loop guard wrote it.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Thrown by <see cref="GcAgentPostSink"/> when the posting agent has no Soul to stamp the post with: it is unbound, its Soul has
/// no version yet, or it is retired (docs/design/06 "Retired", docs/design/07 "Versioning"). Nothing was appended. The turn
/// runner ends the turn as refused, like a loop-guard refusal.
/// </summary>
public sealed class AgentPostUnboundException(Guid agentId) : Exception($"Agent {agentId:D} has no Soul binding to stamp the post with.")
{
    /// <summary>The agent that tried to post.</summary>
    public Guid AgentId { get; } = agentId;
}

/// <summary>
/// Settings for agent posts. <see cref="RequireSoulBinding"/> is the operator's bootstrap switch: with it off, an unbound
/// agent may still post and the post carries no Soul stamp. The shipped default is on.
/// </summary>
public sealed record GcAgentPostOptions
{
    /// <summary>The configuration section (<c>gc.require_soul_binding</c> is <c>RequireSoulBinding</c> here).</summary>
    public const string SectionName = "Bazaar:Gc:AgentPost";

    /// <summary>Refuse a post from an agent with no resolvable Soul. Default true; turn off only while onboarding agents.</summary>
    public bool RequireSoulBinding { get; init; } = true;

    /// <summary>Reads the options from <see cref="SectionName"/>. A missing or malformed value keeps the safe default (required).</summary>
    public static GcAgentPostOptions From(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        var raw = configuration?.GetSection(SectionName)["RequireSoulBinding"];
        return bool.TryParse(raw, out var required) ? new GcAgentPostOptions { RequireSoulBinding = required } : new GcAgentPostOptions();
    }
}
