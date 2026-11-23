using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.VmMounts.Providers.Guest;

/// <summary>Why a guest operation was refused or failed. Stable names for callers and logs.</summary>
public enum MountGuestFailure
{
    /// <summary>The guest path is malformed, or not under the profile folder or a mount of this VM.</summary>
    PathNotAllowed,

    /// <summary>The file is larger than the limit (outbound or inbound).</summary>
    SizeLimit,

    /// <summary>The server-side time limit passed.</summary>
    Timeout,

    /// <summary>No open channel exists for the VM.</summary>
    ChannelUnavailable,

    /// <summary>The guest refused or failed the operation, or its hash check failed.</summary>
    GuestRefused,

    /// <summary>The channel ended or broke.</summary>
    ChannelFailed,

    /// <summary>The caller's arguments are not valid (for example a hash that is not 64 hex digits).</summary>
    InvalidArgument,
}

/// <summary>A guest operation failed. The message is built here from fixed text plus cleaned guest text, never raw guest output.</summary>
public sealed class MountGuestChannelException(MountGuestFailure reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public MountGuestFailure Reason { get; } = reason;
}

/// <summary>
/// Finds the open guest channel of a VM. The transport module opens one channel per VM from a guest connection and a handshake; nothing
/// in the repository yet keeps a registry of live channels, so the host supplies this port (a reported host wiring gap). The default
/// answers null, so every operation fails closed with <see cref="MountGuestFailure.ChannelUnavailable"/>.
/// </summary>
public interface IGuestChannelProvider
{
    /// <summary>The channel of the VM, or null when the VM has no open channel.</summary>
    ValueTask<IGuestChannel?> TryGetAsync(Guid vmId, CancellationToken ct);
}

/// <summary>The shipped <see cref="IGuestChannelProvider"/>: no VM has a channel.</summary>
public sealed class UnavailableGuestChannelProvider : IGuestChannelProvider
{
    public ValueTask<IGuestChannel?> TryGetAsync(Guid vmId, CancellationToken ct) => ValueTask.FromResult<IGuestChannel?>(null);
}

/// <summary>
/// <see cref="IMountGuestChannel"/> over the transport's <see cref="IGuestChannel"/> (doc 16 WP7 and WP8).
/// </summary>
/// <remarks>
/// <para>
/// Mapped: <c>PutFile</c> and <c>GetFile</c> map one to one onto the guest's hash-checked file messages.
/// </para>
/// <para>
/// Not offered, and failing closed with <see cref="NotSupportedException"/>: <see cref="ListFilesAsync"/> (the guest's listing is one
/// directory, names and sizes only, relative to the worktree root; the port needs a recursive list with SHA-256 for an absolute guest
/// path) and <see cref="DeleteDirectoryAsync"/> (the guest deletes one file under the worktree root, never a directory, and not an absolute
/// path). No wire message is added here. Until the guest protocol gains them, a Soul attach cannot copy the cookie profile in and the
/// manager rolls it back; the workspace mount does not use the channel.
/// </para>
/// <para>
/// Guest paths are never taken on trust. Each path must be a well-formed absolute path for the VM's own OS (read from the store, never
/// from the caller), free of <c>..</c>, links to be resolved, alternate streams and control characters, and must lie under the cookie
/// profile folder of the server's layout or under a mount that is attached to this VM (writes: read-write mounts only). Every call has the
/// server's time limit and size cap; a guest message is cleaned before it reaches an exception.
/// </para>
/// </remarks>
public sealed class GuestChannelMountAdapter : IMountGuestChannel
{
    private readonly IGuestChannelProvider _channels;
    private readonly IScopedMountStore _store;
    private readonly IGuestMountLayoutProvider _layouts;
    private readonly VmMountOptions _options;

    public GuestChannelMountAdapter(
        IGuestChannelProvider channels, IScopedMountStore store, IGuestMountLayoutProvider layouts, VmMountOptions options)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(options);
        _channels = channels;
        _store = store;
        _layouts = layouts;
        _options = options;
    }

    public async Task PutFileAsync(Guid vmId, string guestPath, Stream content, string sha256, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(guestPath);
        var hash = NormalizeHash(sha256);
        await CheckPathAsync(vmId, guestPath, write: true, ct).ConfigureAwait(false);
        var max = _options.ProfileLimits.MaxFileBytes;

        // The wire announces the size first, so the stream must be seekable. A stream that is not is buffered, within the cap.
        MemoryStream? buffer = null;
        try
        {
            Stream source = content;
            if (!content.CanSeek)
            {
                buffer = await BufferAsync(content, max, ct).ConfigureAwait(false);
                source = buffer;
            }
            else if (content.Length - content.Position > max)
            {
                throw new MountGuestChannelException(MountGuestFailure.SizeLimit, $"The file is larger than {max} bytes.");
            }

            var channel = await RequireChannelAsync(vmId, ct).ConfigureAwait(false);
            await RunAsync(t => channel.PutFileAsync(guestPath, source, hash, t), "write the file", ct).ConfigureAwait(false);
        }
        finally
        {
            if (buffer is not null)
                await buffer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(guestPath);
        await CheckPathAsync(vmId, guestPath, write: false, ct).ConfigureAwait(false);
        var channel = await RequireChannelAsync(vmId, ct).ConfigureAwait(false);

        await using var capped = new CappedWriteStream(sink, _options.ProfileLimits.MaxFileBytes);
        await RunAsync(t => channel.GetFileAsync(guestPath, capped, t), "read the file", ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(Guid vmId, string guestDirectory, CancellationToken ct) =>
        throw new NotSupportedException(
            "The guest protocol has no recursive file listing with SHA-256 for an absolute guest path, so the cookie profile cannot be read back. "
            + "No wire message was added; the guest protocol needs one first.");

    public Task DeleteDirectoryAsync(Guid vmId, string guestDirectory, CancellationToken ct) =>
        throw new NotSupportedException(
            "The guest protocol deletes one file under the worktree root only, never a directory or an absolute path, so the guest profile copy "
            + "cannot be removed. No wire message was added; the guest protocol needs one first.");

    private async Task CheckPathAsync(Guid vmId, string guestPath, bool write, CancellationToken ct)
    {
        if (vmId == Guid.Empty)
            throw new MountGuestChannelException(MountGuestFailure.InvalidArgument, "The VM id is not set.");

        var os = await _store.GetVmGuestOsAsync(vmId, ct).ConfigureAwait(false)
            ?? throw new MountGuestChannelException(MountGuestFailure.PathNotAllowed, "The VM does not exist.");
        if (!GuestPathPolicy.IsWellFormed(guestPath, os))
            throw new MountGuestChannelException(MountGuestFailure.PathNotAllowed, $"The guest path '{SafeText.Clean(guestPath)}' is not a well-formed {os} path.");

        if (GuestPathPolicy.IsUnder(_layouts.GetLayout(os).ProfilePath, guestPath, os))
            return;

        var mounts = await _store.ListAttachedAsync(ct).ConfigureAwait(false);
        foreach (var mount in mounts)
        {
            if (mount.VmId != vmId || (write && mount.Mode != MountMode.Rw))
                continue;
            if (GuestPathPolicy.IsUnder(mount.GuestPath, guestPath, os))
                return;
        }

        throw new MountGuestChannelException(
            MountGuestFailure.PathNotAllowed, $"The guest path '{SafeText.Clean(guestPath)}' is not under the profile folder or a mount of this VM.");
    }

    private async Task<IGuestChannel> RequireChannelAsync(Guid vmId, CancellationToken ct) =>
        await _channels.TryGetAsync(vmId, ct).ConfigureAwait(false)
        ?? throw new MountGuestChannelException(MountGuestFailure.ChannelUnavailable, "The VM has no open guest channel.");

    /// <summary>Runs one channel call under the server's time limit and maps the transport's exceptions.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> call, string what, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_options.GuestIoTimeout);
        try
        {
            await call(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MountGuestChannelException(MountGuestFailure.Timeout, $"The guest did not {what} within {_options.GuestIoTimeout}.");
        }
        catch (MountGuestChannelException)
        {
            throw;
        }
        catch (GuestFileException ex)
        {
            throw new MountGuestChannelException(MountGuestFailure.GuestRefused, $"The guest could not {what}: {SafeText.Clean(ex.Message)}", ex);
        }
        catch (GuestChannelException ex)
        {
            throw new MountGuestChannelException(MountGuestFailure.ChannelFailed, $"The guest channel failed while trying to {what}: {SafeText.Clean(ex.Message)}", ex);
        }
    }

    private static string NormalizeHash(string sha256)
    {
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            throw new MountGuestChannelException(MountGuestFailure.InvalidArgument, "The SHA-256 must be 64 hex digits.");
        return sha256.ToLowerInvariant();
    }

    private static async Task<MemoryStream> BufferAsync(Stream content, long max, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw new MountGuestChannelException(MountGuestFailure.SizeLimit, $"The file is larger than {max} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}

/// <summary>Lexical rules for a guest path and the "under this root" test, by whole segments.</summary>
internal static class GuestPathPolicy
{
    private const int _maxLength = 1024;
    private const int _maxDepth = 64;

    public static bool IsWellFormed(string path, GuestOs os)
    {
        if (string.IsNullOrEmpty(path) || path.Length > _maxLength)
            return false;
        foreach (var c in path)
        {
            if (c < 0x20 || c == 0x7F)
                return false;
        }

        var segments = Segments(path, os);
        if (segments is null || segments.Length > _maxDepth)
            return false;
        // Segments are checked by the same name rules as the host side: no "..", no trailing dot or space, no reserved names, no streams.
        return segments.All(SafeNames.IsSafeSegment);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it, by whole segments. Both must be well formed.</summary>
    public static bool IsUnder(string root, string path, GuestOs os)
    {
        var r = Segments(root, os);
        var p = Segments(path, os);
        if (r is null || p is null || p.Length < r.Length)
            return false;
        var cmp = os == GuestOs.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < r.Length; i++)
        {
            if (!r[i].Equals(p[i], cmp))
                return false;
        }

        return true;
    }

    /// <summary>The segments after the root marker (a leading <c>/</c> or <c>X:\</c>), or null when the path has the wrong shape for the OS.</summary>
    private static string[]? Segments(string path, GuestOs os)
    {
        string rest;
        char separator;
        if (os == GuestOs.Linux)
        {
            if (path[0] != '/' || path.Contains('\\', StringComparison.Ordinal))
                return null;
            rest = path[1..];
            separator = '/';
        }
        else
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/', StringComparison.Ordinal))
                return null;
            rest = path[3..];
            separator = '\\';
        }

        rest = rest.TrimEnd(separator);
        if (rest.Length == 0)
        {
            // The drive or filesystem root itself is never an allowed guest path.
            return null;
        }

        var parts = rest.Split(separator);
        return parts.Any(string.IsNullOrEmpty) ? null : parts;
    }
}

/// <summary>A write-only stream that passes bytes to another and fails once more than a fixed number have been written.</summary>
internal sealed class CappedWriteStream(Stream inner, long max) : Stream
{
    private long _written;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Count(buffer.Length);
        inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Count(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Count(int length)
    {
        _written += length;
        if (_written > max)
            throw new MountGuestChannelException(MountGuestFailure.SizeLimit, $"The guest sent more than {max} bytes.");
    }
}
