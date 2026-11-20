namespace Bazaar.Contracts.Gateway;

/// <summary>
/// One batch embedding call submitted to the gateway (docs/design/05-model-gateway.md, Embeddings). It takes the same
/// path as a chat call: <c>Route</c> is a model id or a route name that the agent's policy allows, and the gateway
/// resolves it to policy-allowed candidates. <c>Dimensions</c> asks the provider for shorter vectors where it supports
/// that; leave it null to take the model's native size. <c>Route</c> is optional: null or empty means the install's embedding
/// route, the setting <c>gateway.embedding.route</c>; an explicit route still goes through the agent's policy.
/// </summary>
public sealed record EmbeddingRequest(
    string AgentId,
    string? Route,
    IReadOnlyList<string> Inputs,
    int? Dimensions = null);
