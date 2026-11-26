using Bazaar.Server.Coding.Workspace;
using Bazaar.Server.CodingEdit;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Binds the edit module's <see cref="IGuestFileChannel"/> to the guest channel (doc 16). Get and put map directly. The
/// other three operations are not in the guest protocol yet, so they refuse (fail closed) rather than guess.
/// </summary>
public sealed class GuestChannelFileAdapter : IGuestFileChannel
{
    private readonly IGuestChannel _channel;

    /// <summary>Creates the adapter over one guest's channel.</summary>
    public GuestChannelFileAdapter(IGuestChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetFileAsync(string guestPath, CancellationToken ct)
    {
        using var sink = new MemoryStream();
        try
        {
            await _channel.GetFileAsync(guestPath, sink, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // The guest reports a missing file this way; the edit module reads "missing" as null.
            return null;
        }

        return sink.ToArray();
    }

    /// <inheritdoc />
    public async Task PutFileAsync(string guestPath, byte[] content, string sha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var source = new MemoryStream(content, writable: false);
        await _channel.PutFileAsync(guestPath, source, sha256, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The guest protocol has no delete yet.</exception>
    public Task DeleteFileAsync(string guestPath, CancellationToken ct) =>
        throw NotInProtocol("delete");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The guest protocol has no directory listing yet.</exception>
    public Task<IReadOnlyList<string>> ListFilesAsync(string guestDirectory, CancellationToken ct) =>
        throw NotInProtocol("list");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The guest protocol has no real-path resolution yet.</exception>
    public Task<string?> ResolveRealPathAsync(string guestPath, CancellationToken ct) =>
        throw NotInProtocol("resolve-real-path");

    // TODO(doc 16): bind delete, list and resolve-real-path once they are added to the guest protocol. Until then edits that need
    // them (symlink checks above all) are refused, never approximated.
    private static NotSupportedException NotInProtocol(string operation) =>
        new($"The guest file operation '{operation}' is not in the guest protocol yet (doc 16), so it is refused.");
}
