using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Gc;

/// <summary>
/// Asks the server which GCs the signed-in person is a member of, and which one to open by default (request <c>gc.list</c>,
/// docs/design/02, GC list) [PROPOSED].
/// </summary>
public interface IGcListSource
{
    /// <summary>Lists the caller's GCs, most recently active first, with the server's default GC id.</summary>
    /// <exception cref="WireRequestException">The server refused the request.</exception>
    Task<GcListReply> ListAsync(int limit = GcListSource.DefaultLimit, bool includeThinking = false, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IGcListSource"/> over the socket client's request call.</summary>
public sealed class GcListSource(IWireRequester requests) : IGcListSource
{
    /// <summary>The limit used when the caller names none.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The server's largest accepted limit.</summary>
    public const int MaxLimit = 200;

    /// <inheritdoc />
    public Task<GcListReply> ListAsync(int limit = DefaultLimit, bool includeThinking = false, CancellationToken cancellationToken = default)
    {
        // Clamp here so a stray value never costs a round trip that the server would refuse.
        var clamped = Math.Clamp(limit, 1, MaxLimit);
        return requests.RequestAsync<GcListRequest, GcListReply>(WireTypes.GcList, new GcListRequest(clamped, includeThinking), cancellationToken);
    }
}
