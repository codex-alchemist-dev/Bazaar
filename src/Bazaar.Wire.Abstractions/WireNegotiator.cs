using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Wire.Abstractions;

/// <summary>Pure negotiation rules from doc 90 section 4. No I/O.</summary>
public static class WireNegotiator
{
    /// <summary>WebSocket subprotocol prefix for Mode A codec selection.</summary>
    public const string SubprotocolPrefix = "bazaar.v1.";

    /// <summary>
    /// Picks the first client-offered codec the server also has enabled. The client's order wins, the server never
    /// picks something the client did not offer. Null when nothing overlaps.
    /// </summary>
    public static string? PickCodec(IReadOnlyList<string> clientOffered, IReadOnlyCollection<string> serverEnabled)
    {
        ArgumentNullException.ThrowIfNull(clientOffered);
        ArgumentNullException.ThrowIfNull(serverEnabled);
        foreach (var name in clientOffered)
        {
            if (serverEnabled.Contains(name, StringComparer.Ordinal)) return name;
        }

        return null;
    }

    /// <summary>
    /// Orders usable offers by the client's transport preference. Offers whose transport the client does not
    /// have installed or does not support are dropped; unknown preference sorts last, keeping server order.
    /// </summary>
    public static IReadOnlyList<TransportOffer> OrderOffers(
        IReadOnlyList<TransportOffer> offers,
        IReadOnlyList<string> clientOrder,
        Func<string, bool> isSupported)
    {
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(clientOrder);
        ArgumentNullException.ThrowIfNull(isSupported);
        return [.. offers
            .Where(o => isSupported(o.Transport))
            .Select((o, i) => (Offer: o, Index: i, Rank: RankOf(clientOrder, o.Transport)))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Select(x => x.Offer)];
    }

    /// <summary>Formats a Mode A WebSocket subprotocol list, preferred first.</summary>
    public static string FormatSubprotocols(IEnumerable<string> codecs) =>
        string.Join(", ", codecs.Select(c => SubprotocolPrefix + c));

    /// <summary>Reads the codec names from a subprotocol header; names without the prefix are ignored.</summary>
    public static IReadOnlyList<string> ParseSubprotocols(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return [];
        var result = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(SubprotocolPrefix, StringComparison.Ordinal) && part.Length > SubprotocolPrefix.Length)
            {
                result.Add(part[SubprotocolPrefix.Length..]);
            }
        }

        return result;
    }

    /// <summary>
    /// SHA-256 over the client's offered transports and codecs, echoed in <c>welcome</c> so a client can detect a
    /// tampered list on a plaintext overlay (doc 90 section 4.4). Order is part of the input.
    /// </summary>
    public static string OfferHash(IReadOnlyList<string> transports, IReadOnlyList<string> codecs)
    {
        var text = string.Join(',', transports) + "|" + string.Join(',', codecs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static int RankOf(IReadOnlyList<string> order, string transport)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], transport, StringComparison.Ordinal)) return i;
        }

        return int.MaxValue;
    }
}
