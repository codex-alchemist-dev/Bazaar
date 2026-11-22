using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Quarantine;

/// <summary>One item that could enter an agent's context, with what the server knows about it.</summary>
/// <typeparam name="T">The item as the context builder holds it.</typeparam>
/// <param name="Item">The item itself.</param>
/// <param name="Trust">Trust tag assigned at ingestion. Agents cannot set it.</param>
/// <param name="Quarantine">Quarantine status of the item's subject, or null when it has no record.</param>
public sealed record ContextCandidate<T>(T Item, ContentTrust Trust, QuarantineStatus? Quarantine);
