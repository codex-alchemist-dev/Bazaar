using System.Text.Json;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Community.Tools;

/// <summary>
/// Reads the calling session's taint lineage from the session taint reader (doc 15) and returns the source ids as a JSON array of
/// GUID strings, the form the community action log stores (doc 36, "Action log", PROPOSED). A clean session gives "[]". The reader is
/// the same one the tool policy engine uses, so the log and the approval gate see one lineage.
/// </summary>
public sealed class SessionCommunityTaintSourceReader(ISessionTaintReader sessions) : ICommunityTaintSourceReader
{
    private readonly ISessionTaintReader _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public async Task<string> ReadJsonAsync(Guid sessionId, CancellationToken ct = default)
    {
        var taint = await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(taint.Sources.Select(s => s.SourceId.ToString("D")));
    }
}
