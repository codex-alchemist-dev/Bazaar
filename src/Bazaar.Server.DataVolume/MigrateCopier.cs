using System.Security.Cryptography;
using System.Text.Json;

namespace Bazaar.Server.DataVolume;

/// <summary>What the old data root holds, found without following any link and without writing anything.</summary>
internal sealed record MigrateScan(List<ScanFile> Files, List<string> Directories, long TotalBytes, List<string> Refusals);

internal sealed record ScanFile(string RelativePath, long Length);

/// <summary>
/// The file work of <c>storage migrate</c> (doc 63 section 10): scan the old root, copy with a SHA-256 manifest, verify the copy.
/// The old root is only ever read. The top-level <c>keys</c> folder is the KEK's and is handled by the command, not copied.
/// </summary>
internal static class MigrateCopier
{
    public const string ManifestName = ".bazaar-migrate-manifest.json";
    private const string _tempSuffix = ".bazaar-copy.tmp";
    private const string _keysFolder = "keys";

    // What a file system adds to its own root; never part of the data.
    private static readonly string[] _fileSystemRootEntries = ["lost+found", "System Volume Information", "$RECYCLE.BIN"];

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static MigrateScan Scan(string from, CancellationToken cancellationToken)
    {
        var scan = new MigrateScan([], [], 0, []);
        var root = new DirectoryInfo(from);
        Walk(root, string.Empty, from, scan, cancellationToken);
        scan.Files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        scan.Directories.Sort(StringComparer.Ordinal);
        return scan with { TotalBytes = scan.Files.Sum(f => f.Length) };
    }

    private static void Walk(DirectoryInfo directory, string relative, string from, MigrateScan scan, CancellationToken cancellationToken)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (relative.Length == 0 && entry.Name.Equals(_keysFolder, PathComparison))
            {
                CheckKeysFolder(entry, scan);
                continue;
            }

            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var inside = TryResolveInside(entry, from);
                scan.Refusals.Add(inside
                    ? $"'{name}' is a link (symlink or reparse point) inside the old root; links are not copied."
                    : $"'{name}' is a link (symlink or reparse point) that leaves the old root; it is refused.");
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                scan.Directories.Add(name);
                Walk(child, name, from, scan, cancellationToken);
            }
            else if (entry is FileInfo file)
            {
                scan.Files.Add(new ScanFile(name, file.Length));
            }
        }
    }

    private static void CheckKeysFolder(FileSystemInfo entry, MigrateScan scan)
    {
        if (entry is not DirectoryInfo keys || keys.LinkTarget is not null)
        {
            scan.Refusals.Add("'keys' is not a plain folder.");
            return;
        }

        var others = keys.EnumerateFileSystemInfos().Where(e => !StorageCommand.IsKekFileName(e.Name)).Select(e => e.Name).ToList();
        if (others.Count > 0)
            scan.Refusals.Add($"'keys' holds {others.Count} entr{(others.Count == 1 ? "y" : "ies")} besides the KEK files ({string.Join(", ", others.Take(5))}); only the KEK moves to the boot root, so nothing else may be left behind.");
    }

    private static bool TryResolveInside(FileSystemInfo entry, string from)
    {
        try
        {
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && StorageCommand.IsInside(from, target.FullName);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Copies every scanned file into <paramref name="to"/>, hashing what it reads, then writes the manifest. Re-running overwrites; nothing is skipped on trust.</summary>
    public static async Task CopyAsync(string from, string to, MigrateScan scan, CancellationToken cancellationToken)
    {
        DeleteTempFiles(to);

        foreach (var directory in scan.Directories.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal))
            Directory.CreateDirectory(Path.Combine(to, Native(directory)));

        var manifest = new MigrateManifest(1, [], [.. scan.Directories]);
        var buffer = new byte[1024 * 1024];
        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(from, Native(file.RelativePath));
            var destination = Path.Combine(to, Native(file.RelativePath));
            var temp = destination + _tempSuffix;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    length += read;
                }

                output.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, File.GetUnixFileMode(source));

            File.Move(temp, destination, overwrite: true);
            manifest.Files.Add(new ManifestFile(file.RelativePath, length, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }

        await AtomicFile.WriteAsync(
            Path.Combine(to, ManifestName),
            JsonSerializer.SerializeToUtf8Bytes(manifest, MigrateJsonContext.Default.MigrateManifest),
            overwrite: true,
            cancellationToken);

        // Folder modes and times last, deepest first, so writing into a folder never changes what was just set.
        foreach (var directory in scan.Directories.OrderByDescending(d => d.Count(c => c == '/')))
        {
            var source = Path.Combine(from, Native(directory));
            var destination = Path.Combine(to, Native(directory));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));

            Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
        }
    }

    /// <summary>Re-hashes every destination file against the manifest and compares the file set. Returns the problems; empty means the copy is exact.</summary>
    public static async Task<List<string>> VerifyAsync(string to, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        MigrateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(to, ManifestName), cancellationToken), MigrateJsonContext.Default.MigrateManifest);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [$"the copy manifest is missing or unreadable: {e.Message}"];
        }

        if (manifest is null)
            return ["the copy manifest is empty."];

        var buffer = new byte[1024 * 1024];
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(to, Native(file.Path));
            if (!File.Exists(path))
            {
                problems.Add($"missing: {file.Path}");
                continue;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
            }

            if (length != file.Length)
                problems.Add($"length differs: {file.Path} (expected {file.Length}, found {length})");
            else if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
                problems.Add($"hash differs: {file.Path}");
        }

        var expectedFiles = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var expectedDirectories = manifest.Directories.ToHashSet(StringComparer.Ordinal);
        foreach (var directory in manifest.Directories)
        {
            if (!Directory.Exists(Path.Combine(to, Native(directory))))
                problems.Add($"missing folder: {directory}");
        }

        foreach (var (relative, isDirectory) in EnumerateDestination(to, string.Empty, problems))
        {
            if (!(isDirectory ? expectedDirectories : expectedFiles).Contains(relative))
                problems.Add($"unexpected {(isDirectory ? "folder" : "file")}: {relative}");
        }

        return problems;
    }

    private static IEnumerable<(string Relative, bool IsDirectory)> EnumerateDestination(string root, string relative, List<string> problems)
    {
        var directory = relative.Length == 0 ? root : Path.Combine(root, Native(relative));
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var name = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (relative.Length == 0 && (entry.Name is VolumeMarker.FileName or ManifestName || _fileSystemRootEntries.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
                continue;

            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                problems.Add($"unexpected link: {name}");
                continue;
            }

            if (entry is DirectoryInfo)
            {
                yield return (name, true);
                foreach (var child in EnumerateDestination(root, name, problems))
                    yield return child;
            }
            else
            {
                yield return (name, false);
            }
        }
    }

    private static void DeleteTempFiles(string to)
    {
        if (!Directory.Exists(to))
            return;

        foreach (var stale in Directory.EnumerateFiles(to, "*" + _tempSuffix, SearchOption.AllDirectories))
            File.Delete(stale);
    }

    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
}
