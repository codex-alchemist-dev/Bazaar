using Bazaar.Contracts.Engines;

namespace Bazaar.Engines.Grants;

/// <summary>One row of <c>engine_grants</c>: the right of an agent to call one adapter tool at one risk class.</summary>
/// <param name="GrantedBy">Free text until doc 15's approval table settles the schema (TODO(component)).</param>
public sealed record EngineGrant(
    Guid AgentId,
    string AdapterId,
    string ToolName,
    RiskClass RiskClass,
    string GrantedBy,
    DateTimeOffset GrantedAt);
