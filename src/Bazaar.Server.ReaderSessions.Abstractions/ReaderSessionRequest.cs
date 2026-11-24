using System.Collections.Immutable;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.ReaderSessions;

/// <summary>
/// Input to a reader session. The text is always untrusted: <see cref="ReaderInput"/> carries the external trust tag and no
/// other can be made. A reader session has no tools and no credentials, so the request has no way to grant any; the two
/// request lists exist only so an attempt to attach them is visible and refused (doc 15).
/// </summary>
/// <param name="AgentId">The agent the session runs for. The gateway applies that agent's model policy.</param>
/// <param name="Purpose">Short label for audit, such as <c>twin-prose</c>. Never shown to the model as an instruction.</param>
/// <param name="ModelPolicyKey">The gateway route (a model id or a route name) the agent's policy resolves.</param>
/// <param name="Input">The untrusted text and its lineage.</param>
/// <param name="HandoverSchemaId">A registered hand-over schema id for structured mode, or null for plain-text mode.</param>
/// <param name="Instructions">Server-authored task instructions (system section). Never built from untrusted text.</param>
/// <param name="RequestedTools">Tools a caller tried to attach. Any entry refuses the request.</param>
/// <param name="RequestedCredentials">Credentials a caller tried to attach. Any entry refuses the request.</param>
public sealed record ReaderSessionRequest(
    string AgentId,
    string Purpose,
    string ModelPolicyKey,
    ReaderInput Input,
    string? HandoverSchemaId = null,
    string? Instructions = null,
    IReadOnlyList<string>? RequestedTools = null,
    IReadOnlyList<string>? RequestedCredentials = null);

/// <summary>Untrusted text for a reader session, with the source items it came from (lineage for the result).</summary>
public sealed class ReaderInput
{
    private ReaderInput(string text, ImmutableArray<Guid> sourceRefs)
    {
        Text = text;
        SourceRefs = sourceRefs;
    }

    /// <summary>The text. Treated as data, never as instructions.</summary>
    public string Text { get; }

    /// <summary>Always <see cref="ContentTrust.External"/>.</summary>
    public ContentTrust Trust { get; } = ContentTrust.External;

    /// <summary>The source items the text was drawn from. Required for structured mode (a hand-over needs lineage).</summary>
    public ImmutableArray<Guid> SourceRefs { get; }

    /// <summary>Marks text as untrusted input. Blank text is allowed here and refused by the runner.</summary>
    public static ReaderInput Untrusted(string text, IEnumerable<Guid>? sourceRefs = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ReaderInput(text, sourceRefs?.Distinct().ToImmutableArray() ?? ImmutableArray<Guid>.Empty);
    }
}
