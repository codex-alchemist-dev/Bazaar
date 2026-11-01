namespace Bazaar.Contracts.Engines;

/// <summary>
/// The fields every adapter message carries (docs/design/20-engine-integration-api.md, Data and interfaces).
/// <c>Seq</c> numbers the adapter's own stream per project. GC sequence numbers are a different counter and are not used here.
/// </summary>
public sealed record EngineProjectEnvelope(
    string ProjectId,
    string CommandId,
    long Seq);
