using System.Net;
using Bazaar.Conduit.PeerToPeer;

namespace Bazaar.Conduit.Tests.PeerToPeer;

/// <summary>Scripted STUN transport. Records every server asked and answers with <paramref name="respond"/>.</summary>
internal sealed class FakeStunTransport(Func<IPEndPoint, byte[], byte[]?> respond) : IStunTransport
{
    /// <summary>Servers asked, in order.</summary>
    public List<IPEndPoint> Asked { get; } = [];

    public Task<byte[]?> ExchangeAsync(IPEndPoint server, byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Asked.Add(server);
        return Task.FromResult(respond(server, request));
    }

    /// <summary>A transport where every server answers with the given mapped address, echoing the request's transaction id.</summary>
    public static FakeStunTransport Answering(IPEndPoint mapped) =>
        new((_, request) => StunFixtures.SuccessResponse(request.AsSpan(8, 12), mapped));

    /// <summary>A transport where no server answers.</summary>
    public static FakeStunTransport Silent() => new((_, _) => null);
}
