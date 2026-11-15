namespace Bazaar.Portability.Client;

/// <summary>
/// The owner's per-item choice for one collision in the preview: keep <see cref="KeepSourceKey"/> and skip the
/// other records with the same target and title. A collision with no decision keeps every record (doc 44 section 8.10).
/// </summary>
public sealed record CollisionDecision(string Target, string Title, string KeepSourceKey);
