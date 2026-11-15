namespace Bazaar.Portability.Client;

/// <summary>The per-item choice for one colliding record (doc 44 section 8.10). <see cref="KeepBoth"/> is the default.</summary>
public enum CollisionAction
{
    KeepBoth,
    SkipIncoming,
    Replace,
}

/// <summary>A non-default choice for one colliding record. Records with no decision are kept and flagged.</summary>
public sealed record CollisionDecision(string Target, string Title, string SourceKey, CollisionAction Action);
