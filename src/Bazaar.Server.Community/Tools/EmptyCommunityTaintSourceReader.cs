namespace Bazaar.Server.Community.Tools;

/// <summary>Taint reader for a host with no session taint reader: reports no sources.</summary>
internal sealed class EmptyCommunityTaintSourceReader : ICommunityTaintSourceReader
{
    public Task<string> ReadJsonAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult("[]");
}
