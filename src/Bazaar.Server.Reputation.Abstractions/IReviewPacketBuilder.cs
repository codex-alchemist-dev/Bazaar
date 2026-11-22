namespace Bazaar.Server.Reputation;

/// <summary>Builds the blinded packet a reviewer sees. Server-side only (doc 11, "Review packet").</summary>
public interface IReviewPacketBuilder
{
    /// <summary>Maps <paramref name="subject"/> to a packet without the author's identity.</summary>
    ReviewPacketResult Build(ReviewSubject subject);
}
