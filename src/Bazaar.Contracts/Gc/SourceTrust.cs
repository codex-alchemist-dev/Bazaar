using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gc;

/// <summary>
/// Where a piece of content came from (docs/design/15-security-taint-and-approvals.md). The server
/// sets it at ingestion; clients and agents cannot set it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceTrust>))]
public enum SourceTrust
{
    /// <summary>Authenticated owner.</summary>
    [JsonStringEnumMemberName("owner")]
    Owner,

    /// <summary>Server-generated.</summary>
    [JsonStringEnumMemberName("system")]
    System,

    /// <summary>Authenticated co-member (a human who is not the owner).</summary>
    [JsonStringEnumMemberName("member-human")]
    MemberHuman,

    /// <summary>Another agent's message or output, including subagent output.</summary>
    [JsonStringEnumMemberName("agent")]
    Agent,

    /// <summary>Email, strangers' issues and PRs, web, forums, package text, and their tool output. Taints hard.</summary>
    [JsonStringEnumMemberName("external")]
    External,

    /// <summary>Flagged content or agent. Never delivered to context builders.</summary>
    [JsonStringEnumMemberName("quarantined")]
    Quarantined,
}
