namespace Bazaar.Conduit.PeerToPeer;

/// <summary>One signaling message exchanged between two peers over a rendezvous channel (doc 39 work package 5).</summary>
/// <param name="Kind">One of the <see cref="SignalKinds"/> values.</param>
/// <param name="SessionId">Identifies the rendezvous session both peers share. Messages for other sessions are ignored.</param>
/// <param name="Payload">Kind-specific text. For <see cref="SignalKinds.Candidate"/> it is an endpoint in "ip:port" form.</param>
public sealed record SignalMessage(string Kind, string SessionId, string Payload);

/// <summary>The message kinds of the signaling protocol.</summary>
public static class SignalKinds
{
    /// <summary>Session description offered by the initiating peer. Reserved for the QUIC stage.</summary>
    public const string Offer = "offer";

    /// <summary>Session description answered by the responding peer. Reserved for the QUIC stage.</summary>
    public const string Answer = "answer";

    /// <summary>A reflexive or host candidate, as an endpoint string.</summary>
    public const string Candidate = "candidate";

    /// <summary>The sender is leaving the session.</summary>
    public const string End = "end";

    /// <summary>Returns true for the four known kinds.</summary>
    public static bool IsKnown(string kind) =>
        kind is Offer or Answer or Candidate or End;
}
