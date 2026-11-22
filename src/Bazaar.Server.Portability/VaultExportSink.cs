using System.Security.Cryptography;

namespace Bazaar.Server.Portability;

/// <summary>
/// Where a Vault export writes its files. The exporter opens one stream per file, writes it and disposes it. A sink must
/// refuse to replace an existing file.
/// </summary>
public interface IVaultExportSink
{
    /// <summary>Opens a new file at <paramref name="relativePath"/> for writing. The caller disposes the stream.</summary>
    Task<Stream> OpenWriteAsync(string relativePath, CancellationToken ct);
}

/// <summary>Writes a Vault export as a plain folder tree under one root directory. Never overwrites a file.</summary>
public sealed class DirectoryVaultExportSink : IVaultExportSink
{
    private readonly string _root;

    public DirectoryVaultExportSink(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    public Task<Stream> OpenWriteAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = VaultExportPaths.ResolveUnder(_root, relativePath);
        var folder = Path.GetDirectoryName(target);
        if (folder is not null) Directory.CreateDirectory(folder);

        // FileMode.CreateNew fails when the file already exists, so an export never replaces a file.
        Stream stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }
}

/// <summary>Path rules for export output (doc 44 section 5 applies the same rules to bundle paths).</summary>
internal static class VaultExportPaths
{
    /// <summary>Rejects absolute paths, drive or scheme forms, backslashes, empty segments and '.' or '..' segments.</summary>
    public static void Validate(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Export path is empty.");
        if (path.Contains('\0', StringComparison.Ordinal)) throw new ArgumentException("Export path has a NUL character.");
        if (path.Contains('\\', StringComparison.Ordinal)) throw new ArgumentException("Export path has a backslash: " + path);
        if (path.StartsWith('/') || Path.IsPathRooted(path)) throw new ArgumentException("Export path is absolute: " + path);
        if (path.Contains(':', StringComparison.Ordinal)) throw new ArgumentException("Export path has a drive or scheme: " + path);

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) throw new ArgumentException("Export path has an empty segment: " + path);
            if (segment is "." or "..") throw new ArgumentException("Export path has a traversal segment: " + path);
        }
    }

    /// <summary>Validates <paramref name="path"/> and returns its full location under <paramref name="root"/>, which must be a full path.</summary>
    public static string ResolveUnder(string root, string path)
    {
        Validate(path);
        var target = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Export path escapes the root: " + path);
        }
        return target;
    }
}

/// <summary>
/// A write-only stream that forwards to <paramref name="inner"/> and records the SHA-256 and length of what passed through,
/// so a file can be written and hashed in one pass. It does not dispose <paramref name="inner"/>.
/// </summary>
internal sealed class DigestingWriteStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesWritten;

    public long BytesWritten => _bytesWritten;

    /// <summary>The SHA-256 of everything written, lowercase hex. Call once, after the last write and flush.</summary>
    public string Sha256Hex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

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
        _hash.AppendData(buffer);
        _bytesWritten += buffer.Length;
        inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        _hash.AppendData(buffer.Span);
        _bytesWritten += buffer.Length;
        return inner.WriteAsync(buffer, cancellationToken);
    }
}
