namespace Bazaar.Server.Security.Taint;

/// <summary>Content arriving in a session, with the lineage the server tracked for it.</summary>
/// <param name="SourceId">Id of the event, tool result or message.</param>
/// <param name="Trust">Trust tag assigned at ingestion.</param>
/// <param name="SourceState">
/// Taint of the session that produced the content, for agent text. Ignored for other trust tags.
/// </param>
/// <param name="InheritsExternal">
/// True when server-tracked lineage shows the content quotes or was built from external text. Such
/// content is treated as <see cref="ContentTrust.External"/> whatever its own tag says.
/// </param>
public sealed record TaintInput(
    Guid SourceId,
    ContentTrust Trust,
    TaintState SourceState = TaintState.Clean,
    bool InheritsExternal = false);
