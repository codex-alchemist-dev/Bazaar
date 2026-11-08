using System.Collections.Immutable;

namespace Bazaar.Server.Sanitization;

/// <summary>Sanitized text and a per-kind count of what was removed.</summary>
/// <param name="Text">The text with placeholders in place of removed spans.</param>
/// <param name="Redactions">One entry per kind removed, ordered by kind. Empty when nothing was removed.</param>
public sealed record SanitizedText(string Text, ImmutableArray<Redaction> Redactions);
