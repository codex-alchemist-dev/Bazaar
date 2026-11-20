using System.Formats.Tar;
using System.IO.Compression;

namespace Bazaar.Client.Updater.Linux;

/// <summary>The tarball is malformed, too large, or would write outside its release directory.</summary>
public sealed class ArchiveRejectedException : Exception
{
    /// <summary>Creates the exception with a reason that is safe to log.</summary>
    public ArchiveRejectedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Extracts a <c>tar.gz</c> into one directory and nothing else. It accepts directories, regular files and symbolic links.
/// Hard links, devices and FIFOs are rejected. An entry name must stay inside the destination (no absolute path, no
/// <c>..</c>, no backslash or NUL); a symbolic link must be relative and, resolved against its own directory, stay inside;
/// no entry may be written through a link the archive created. Modes keep the owner, group and other read and execute bits and
/// the owner write bit, and drop setuid, setgid, sticky and group or other write. Entry count and expanded size are bounded.
/// </summary>
public sealed class SafeTarGzExtractor
{
    private const UnixFileMode AllowedModes =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly IUpdateFileSystem _fs;
    private readonly long _maxBytes;
    private readonly int _maxEntries;

    /// <summary>Creates an extractor with the given bounds.</summary>
    public SafeTarGzExtractor(IUpdateFileSystem fs, long maxExtractedBytes, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExtractedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _fs = fs;
        _maxBytes = maxExtractedBytes;
        _maxEntries = maxEntries;
    }

    /// <summary>Extracts <paramref name="archive"/> into <paramref name="destination"/>, which must not exist yet.</summary>
    /// <exception cref="ArchiveRejectedException">The archive breaks a rule above or is not a valid tar.gz.</exception>
    public async Task ExtractAsync(Stream archive, string destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        _fs.CreateDirectory(destination, DirectoryMode);
        var links = new HashSet<string>(StringComparer.Ordinal);
        int entries = 0;
        long total = 0;
        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false)) is not null)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                if (++entries > _maxEntries)
                {
                    throw new ArchiveRejectedException($"The archive holds more than {_maxEntries} entries.");
                }

                total = await ExtractEntryAsync(entry, destination, links, total, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw new ArchiveRejectedException("The archive is not a valid tar.gz: " + e.Message);
        }
    }

    private async Task<long> ExtractEntryAsync(TarEntry entry, string destination, HashSet<string> links, long total, CancellationToken ct)
    {
        string[] segments = Normalize(entry.Name);
        if (segments.Length == 0)
        {
            return total; // The archive root itself ("./").
        }

        string relative = string.Join('/', segments);
        string target = UnixPath.Combine(destination, relative);
        for (int i = 1; i <= segments.Length - 1; i++)
        {
            if (links.Contains(string.Join('/', segments, 0, i)))
            {
                throw new ArchiveRejectedException($"Entry '{relative}' would be written through a symbolic link.");
            }
        }

        switch (entry.EntryType)
        {
            case TarEntryType.Directory:
                if (links.Contains(relative))
                {
                    throw new ArchiveRejectedException($"Entry '{relative}' replaces a symbolic link with a directory.");
                }

                _fs.CreateDirectory(target, DirectoryMode);
                return total;

            case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                if (links.Contains(relative))
                {
                    throw new ArchiveRejectedException($"Entry '{relative}' replaces a symbolic link with a file.");
                }

                return await WriteFileAsync(entry, target, relative, total, ct).ConfigureAwait(false);

            case TarEntryType.SymbolicLink:
                CheckLinkTarget(segments, entry.LinkName, relative);
                if (!links.Add(relative) || _fs.FileExists(target) || _fs.DirectoryExists(target))
                {
                    throw new ArchiveRejectedException($"Entry '{relative}' appears twice.");
                }

                CreateParent(target);
                _fs.CreateSymbolicLink(target, entry.LinkName);
                return total;

            default:
                throw new ArchiveRejectedException($"Entry '{relative}' has type {entry.EntryType}, which is not allowed.");
        }
    }

    private async Task<long> WriteFileAsync(TarEntry entry, string target, string relative, long total, CancellationToken ct)
    {
        if (entry.Length < 0 || total + entry.Length > _maxBytes)
        {
            throw new ArchiveRejectedException($"The archive expands beyond {_maxBytes} bytes.");
        }

        CreateParent(target);
        try
        {
            await using (var output = _fs.CreateFile(target))
            {
                if (entry.DataStream is { } data)
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > _maxBytes)
                        {
                            throw new ArchiveRejectedException($"The archive expands beyond {_maxBytes} bytes.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (IOException) when (_fs.FileExists(target))
        {
            throw new ArchiveRejectedException($"Entry '{relative}' appears twice.");
        }

        _fs.SetUnixMode(target, (entry.Mode & AllowedModes) | UnixFileMode.UserRead);
        return total;
    }

    private void CreateParent(string target)
    {
        string? parent = UnixPath.GetDirectoryName(target);
        if (parent is not null)
        {
            _fs.CreateDirectory(parent, DirectoryMode);
        }
    }

    /// <summary>Splits an entry name into clean segments, dropping empty and <c>.</c> parts; rejects anything that could leave the root.</summary>
    private static string[] Normalize(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArchiveRejectedException("An entry has an empty name or a forbidden character.");
        }

        if (name.StartsWith('/'))
        {
            throw new ArchiveRejectedException($"Entry '{name}' has an absolute path.");
        }

        var segments = new List<string>();
        foreach (string part in name.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new ArchiveRejectedException($"Entry '{name}' contains '..'.");
            }

            segments.Add(part);
        }

        return [.. segments];
    }

    /// <summary>
    /// A link must be relative, and its target may climb with leading <c>..</c> segments only. Climbing after a name is refused,
    /// because that name could be another link and the lexical depth would then not be the real one.
    /// </summary>
    private static void CheckLinkTarget(string[] entrySegments, string? linkName, string relative)
    {
        if (string.IsNullOrEmpty(linkName) || linkName.Contains('\0', StringComparison.Ordinal) || linkName.StartsWith('/'))
        {
            throw new ArchiveRejectedException($"Link '{relative}' must have a relative target.");
        }

        int depth = entrySegments.Length - 1;
        bool climbing = true;
        foreach (string part in linkName.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (!climbing || --depth < 0)
                {
                    throw new ArchiveRejectedException($"Link '{relative}' points outside the release.");
                }
            }
            else
            {
                climbing = false;
            }
        }
    }
}
