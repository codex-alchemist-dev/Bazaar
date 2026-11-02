namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>
/// A GC message as the scorer sees it. <paramref name="Text"/> is untrusted content and must only
/// reach the model inside the prompt's data block. <paramref name="SenderKind"/> is a server-assigned label.
/// </summary>
public sealed record GcMessageRef(Guid GcId, long Seq, string Text, string SenderKind);
