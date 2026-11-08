namespace Bazaar.Server.Sanitization;

/// <summary>How many spans of one kind a sanitizer removed. The removed text itself is never kept.</summary>
/// <param name="Kind">The kind of span removed.</param>
/// <param name="Count">The number of spans removed; always at least one.</param>
public sealed record Redaction(RedactionKind Kind, int Count);
