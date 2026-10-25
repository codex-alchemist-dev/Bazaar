namespace Bazaar.Contracts.Gc;

/// <summary>
/// A message as a client shows it: identity plus the current version's text. The log keeps every
/// version (docs/design/01-gc-data-model.md); <see cref="Edited"/> marks messages with more than one.
/// </summary>
/// <param name="GcId">The group chat the message belongs to.</param>
/// <param name="MessageId">Stable message identity across edits.</param>
/// <param name="AuthorId">Principal who wrote the original message.</param>
/// <param name="FirstSeq">Sequence number of the message's first event.</param>
/// <param name="Version">Current version number, starting at 1.</param>
/// <param name="Body">Text of the current version.</param>
/// <param name="Edited">True when the message has more than one version.</param>
/// <param name="CreatedAt">When the original version was written.</param>
public sealed record GcMessage(
    Guid GcId,
    Guid MessageId,
    Guid AuthorId,
    long FirstSeq,
    int Version,
    string Body,
    bool Edited,
    DateTimeOffset CreatedAt);
