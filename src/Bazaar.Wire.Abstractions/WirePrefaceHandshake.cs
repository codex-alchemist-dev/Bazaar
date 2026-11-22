namespace Bazaar.Wire.Abstractions;

/// <summary>Codec handshake over a freshly opened stream (doc 90 section 4.2 Mode A), shared by stream transports.</summary>
public static class WirePrefaceHandshake
{
    /// <summary>Client side: sends the offered codecs, reads the server's choice, and checks it was offered.</summary>
    /// <exception cref="InvalidDataException">The server answered with a name that was not offered, or a malformed preface.</exception>
    public static async ValueTask<string> ClientAsync(Stream stream, IReadOnlyList<string> codecs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(WirePreface.Encode(codecs), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var answer = await WirePreface.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (answer.Count != 1 || !codecs.Contains(answer[0], StringComparer.Ordinal)) throw new InvalidDataException("Server chose a codec that was not offered.");
        return answer[0];
    }

    /// <summary>
    /// Server side: reads the client's preface within <paramref name="timeout"/>, picks the first client codec the
    /// operator enabled, and echoes it. Returns null when nothing matches (the caller closes with 4400).
    /// </summary>
    /// <exception cref="InvalidDataException">Bad magic or malformed names (the caller closes with 4400).</exception>
    /// <exception cref="OperationCanceledException">The timeout elapsed (the caller closes with 4408) or the token was cancelled.</exception>
    public static async ValueTask<string?> ServerAsync(Stream stream, IReadOnlyList<string> enabledCodecs, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(enabledCodecs);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        IReadOnlyList<string> offered;
        try
        {
            offered = await WirePreface.ReadAsync(stream, limit.Token).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Stream ended inside the preface.");
        }

        var chosen = offered.FirstOrDefault(name => enabledCodecs.Contains(name, StringComparer.Ordinal));
        if (chosen is null) return null;
        await stream.WriteAsync(WirePreface.Encode([chosen]), limit.Token).ConfigureAwait(false);
        await stream.FlushAsync(limit.Token).ConfigureAwait(false);
        return chosen;
    }
}
