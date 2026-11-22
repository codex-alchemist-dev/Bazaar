namespace Bazaar.Server.Security.Taint;

/// <summary>One piece of content that raised a session's taint. Kept so the audit trail can show lineage.</summary>
/// <param name="SourceId">Id of the event, tool result or message that carried the taint.</param>
/// <param name="Trust">The trust tag the server assigned to that content.</param>
/// <param name="Raised">The taint level this content added to the session.</param>
public sealed record TaintSource(Guid SourceId, ContentTrust Trust, TaintState Raised);
