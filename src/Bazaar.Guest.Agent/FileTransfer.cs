using System.Security.Cryptography;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// Resolves guest paths against the allowed roots. The path is normalised with Path.GetFullPath, then every symlink on
/// the way is followed, so a link inside a root that points outside it is refused.
/// </summary>
internal sealed class PathGuard
{
    private const int _maxLinkHops = 40;
    private readonly string[] _roots;

    /// <summary>Creates a guard over the given roots.</summary>
    public PathGuard(IEnumerable<string> roots)
    {
        _roots = roots
            .Select(root => RealPath(Path.GetFullPath(root), 0))
            .OfType<string>()
            .ToArray();
    }

    /// <summary>Returns the real path when it lies strictly inside a root (a file path), otherwise null.</summary>
    public string? Resolve(string path) => Locate(path, includeRoot: false);

    /// <summary>Returns the real path when it is a root or lies inside one (a working directory), otherwise null.</summary>
    public string? ResolveDirectory(string path) => Locate(path, includeRoot: true);

    private string? Locate(string path, bool includeRoot)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var real = RealPath(Path.GetFullPath(path), 0);
            return real is not null && _roots.Any(root => IsUnder(real, root, includeRoot)) ? real : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? RealPath(string fullPath, int hops)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, part);
            var linkTarget = new FileInfo(candidate).LinkTarget;
            if (linkTarget is null)
            {
                current = candidate;
                continue;
            }

            if (hops >= _maxLinkHops)
            {
                return null;
            }

            // A relative link target is relative to the directory that holds the link, which is `current`.
            var resolved = RealPath(Path.GetFullPath(linkTarget, current), hops + 1);
            if (resolved is null)
            {
                return null;
            }

            current = resolved;
        }

        return current;
    }

    private static bool IsUnder(string path, string root, bool includeRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Equals(root, comparison))
        {
            return includeRoot;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, comparison);
    }
}

/// <summary>
/// PutFile and GetFile. The content travels as raw bytes after the PutFile frame on the same stream. Refused paths
/// never reach the file system, and the content of a refused PutFile is still read, so the stream stays in step.
/// </summary>
internal sealed class FileTransfer
{
    private const int _copyBytes = 64 * 1024;
    private readonly PathGuard _paths;
    private readonly TextWriter _log;

    /// <summary>Creates the transfer handler.</summary>
    public FileTransfer(PathGuard paths, TextWriter log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// Receives one PutFile and its content, then sends a <see cref="PutFileResult"/>. The file is written to a temporary
    /// name, hashed while it is written, and moved into place only when the SHA-256 matches. On mismatch the partial file
    /// is deleted. Returns false when the stream can no longer be read in step (the caller then closes the channel).
    /// </summary>
    public async Task<bool> ReceiveAsync(PutFile put, Stream input, OutboundQueue outbound, CancellationToken ct)
    {
        if (put.Size < 0)
        {
            _log.WriteLine($"PutFile for '{put.Path}' has a negative size; closing the channel.");
            await outbound.SendAsync(new PutFileResult(put.Path, false, "the size is negative"), ct);
            return false;
        }

        var target = _paths.Resolve(put.Path);
        if (target is null || !IsSha256Hex(put.Sha256))
        {
            var reason = target is null ? "the path is outside the allowed roots" : "the hash is malformed";
            _log.WriteLine($"PutFile refused for '{put.Path}': {reason}.");
            var drained = await DiscardAsync(input, put.Size, ct);
            await outbound.SendAsync(new PutFileResult(put.Path, false, reason), ct);
            return drained;
        }

        var temp = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        FileStream file;
        try
        {
            file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"PutFile could not create a file for '{put.Path}': {ex.Message}");
            var drained = await DiscardAsync(input, put.Size, ct);
            await outbound.SendAsync(new PutFileResult(put.Path, false, "the file could not be created"), ct);
            return drained;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_copyBytes];
        var remaining = put.Size;
        string? writeError = null;
        var streamEnded = false;
        try
        {
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                remaining -= count;
                hash.AppendData(buffer, 0, count);

                if (writeError is null)
                {
                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    catch (IOException ex)
                    {
                        // Keep reading so the stream stays in step, then discard the file below.
                        writeError = ex.Message;
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            streamEnded = true;
        }
        finally
        {
            await file.DisposeAsync();
        }

        if (streamEnded)
        {
            TryDelete(temp);
            await outbound.SendAsync(new PutFileResult(put.Path, false, "the channel ended before the content did"), ct);
            return false;
        }

        if (writeError is not null)
        {
            TryDelete(temp);
            _log.WriteLine($"PutFile for '{put.Path}' failed while writing: {writeError}");
            await outbound.SendAsync(new PutFileResult(put.Path, false, "the file could not be written"), ct);
            return true;
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (actual != put.Sha256)
        {
            TryDelete(temp);
            _log.WriteLine($"PutFile for '{put.Path}' failed the SHA-256 check; the partial file was removed.");
            await outbound.SendAsync(new PutFileResult(put.Path, false, "the SHA-256 does not match the content"), ct);
            return true;
        }

        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            _log.WriteLine($"PutFile for '{put.Path}' could not be moved into place: {ex.Message}");
            await outbound.SendAsync(new PutFileResult(put.Path, false, "the file could not be moved into place"), ct);
            return true;
        }

        await outbound.SendAsync(new PutFileResult(put.Path, true, null), ct);
        return true;
    }

    /// <summary>
    /// Queues a GetFile answer: a PutFile header and the file's bytes, written as one unit by the outbound writer.
    /// A refused path is answered at once with a <see cref="GetFileError"/>. A missing or unreadable file is answered
    /// by the writer in place of the header, so the answer keeps its place in the stream.
    /// </summary>
    public async ValueTask QueueSendAsync(string path, OutboundQueue outbound, CancellationToken ct)
    {
        var target = _paths.Resolve(path);
        if (target is null)
        {
            _log.WriteLine($"GetFile refused for '{path}': the path is outside the allowed roots.");
            await outbound.SendAsync(new GetFileError(path, "the path is outside the allowed roots"), ct);
            return;
        }

        await outbound.EnqueueAsync((stream, token) => SendFileAsync(path, target, stream, token), ct);
    }

    private async Task SendFileAsync(string guestPath, string path, Stream output, CancellationToken ct)
    {
        FileStream source;
        try
        {
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"GetFile could not open '{path}': {ex.Message}");
            await GuestFrameCodec.WriteAsync(output, new GetFileError(guestPath, "the file is missing or cannot be opened"), ct);
            return;
        }

        await using (source)
        {
            // Hash first, because the PutFile header carries the size and hash before the bytes. Both passes read
            // exactly the same byte count, so the hash covers what is sent.
            var size = source.Length;
            var buffer = new byte[_copyBytes];
            string sha256;
            try
            {
                sha256 = await HashAsync(source, size, buffer, ct);
            }
            catch (IOException ex)
            {
                _log.WriteLine($"GetFile could not read '{path}': {ex.Message}");
                await GuestFrameCodec.WriteAsync(output, new GetFileError(guestPath, "the file cannot be read"), ct);
                return;
            }

            source.Position = 0;
            await GuestFrameCodec.WriteAsync(output, new PutFile(guestPath, size, sha256), ct);

            var remaining = size;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                {
                    // The header is already out, so the stream cannot recover. The writer fails and the agent exits.
                    throw new IOException($"'{path}' shrank while it was being sent.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }

            await output.FlushAsync(ct);
        }
    }

    private static async Task<string> HashAsync(Stream source, long size, byte[] buffer, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = size;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                throw new IOException("The file is shorter than its size.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<bool> DiscardAsync(Stream input, long size, CancellationToken ct)
    {
        var buffer = new byte[_copyBytes];
        var remaining = size;
        try
        {
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                remaining -= count;
            }
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        return true;
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the temporary name is unique, so a leftover cannot be picked up by a later transfer.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
