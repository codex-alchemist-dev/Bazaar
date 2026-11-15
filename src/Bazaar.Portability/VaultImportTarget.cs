using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Portability;

/// <summary>
/// Writes import batches as Vault Markdown files and commits each batch through <see cref="IGitProvider"/>
/// (doc 18, the Vault is git). Files go to <c>imports/&lt;job&gt;/&lt;source key&gt;.md</c>. The checkout is a dedicated
/// worktree prepared by the host; each batch writes its files and makes one commit. A batch that fails removes the files
/// it created, so the checkout is left as it was before the batch.
/// </summary>
public sealed class VaultImportTarget(IGitProvider git, string repoPath, string checkoutPath, GitIdentity identity)
{
    /// <summary>The Vault path of one imported record. Rejects keys that could escape the import folder.</summary>
    public static string PathFor(string jobId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        if (jobId.Contains('/', StringComparison.Ordinal) || jobId.Contains('\\', StringComparison.Ordinal) || jobId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Job id is not a safe path segment.", nameof(jobId));
        }
        if (sourceKey.Contains('/', StringComparison.Ordinal) || sourceKey.Contains('\\', StringComparison.Ordinal) || sourceKey.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Source key is not a safe path segment.", nameof(sourceKey));
        }
        return "imports/" + jobId + "/" + sourceKey + ".md";
    }

    /// <summary>Writes one batch and commits it. Throws when any file cannot be written or the commit fails.</summary>
    public async Task WriteBatchAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var created = new List<string>();
        try
        {
            foreach (var item in items)
            {
                if (!string.Equals(item.Provenance.JobId, jobId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Item belongs to a different import job.", nameof(items));
                }

                if (item.Replaces is not null)
                {
                    // Replacing a Vault note is not implemented yet. Refuse the batch rather than write a copy that the audit calls a replace.
                    throw new NotSupportedException("Replace is not implemented for the Vault target; the batch is refused.");
                }

                var full = Path.Combine(checkoutPath, PathFor(jobId, item.Provenance.SourceKey));
                if (File.Exists(full))
                {
                    throw new IOException("Vault path already exists: " + PathFor(jobId, item.Provenance.SourceKey));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, VaultMarkdown.Render(item), new UTF8Encoding(false), ct);
                created.Add(full);
            }

            var message = "import " + jobId + " batch " + batchIndex.ToString(CultureInfo.InvariantCulture)
                + "\n\nBazaar-Import-Job: " + jobId
                + "\nBazaar-Import-Batch: " + batchIndex.ToString(CultureInfo.InvariantCulture);
            await git.CommitAsync(checkoutPath, message, identity, ct);
        }
        catch
        {
            foreach (var path in created)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
    }

    /// <summary>Reads the HEAD commit and returns the source keys of this job's import files.</summary>
    public async Task<IReadOnlyList<string>> ReadSourceKeysAsync(string jobId, CancellationToken ct)
    {
        var head = await git.HeadAsync(checkoutPath, ct);
        var prefix = "imports/" + jobId + "/";
        var keys = new List<string>();
        foreach (var path in await git.ListFilesAsync(repoPath, head, ct))
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            var text = await git.ReadTextAsync(repoPath, head, path, ct);
            var key = VaultMarkdown.ReadSourceKey(text) ?? throw new InvalidDataException("Front matter has no source_key: " + path);
            keys.Add(key);
        }
        return keys;
    }
}

/// <summary>Renders the Markdown of an imported note with doc 18 front matter. Output is byte-stable.</summary>
public static class VaultMarkdown
{
    /// <summary>The document type of imported notes. Imported text is unreviewed, so it is a draft.</summary>
    public const string ImportType = "draft";

    /// <summary>The trust label of imported text (doc 15, doc 44 section 8.5).</summary>
    public const string ImportTrust = "external";

    private const string _crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Renders the file: a front matter block (id, type, title, source_trust, imported_job, source_key) and the body, with LF
    /// line endings and no byte-order mark. Keys are fixed in this order.
    /// </summary>
    public static string Render(ImportItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var jobId = item.Provenance.JobId;
        var sourceKey = item.Provenance.SourceKey;
        var front = new VaultFrontMatter(
            Id: UlidFor(jobId, sourceKey),
            Type: ImportType,
            Title: SafeTitle(item.Record.Title, sourceKey),
            Project: null,
            Status: null,
            Aliases: [],
            Owner: null,
            SourceTrust: ImportTrust,
            Tier: null);

        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(front.Id).Append('\n');
        builder.Append("type: ").Append(front.Type).Append('\n');
        builder.Append("title: ").Append(front.Title).Append('\n');
        builder.Append("source_trust: ").Append(front.SourceTrust).Append('\n');
        builder.Append("imported_job: ").Append(jobId).Append('\n');
        builder.Append("source_key: ").Append(sourceKey).Append('\n');
        builder.Append("---\n");
        builder.Append(item.Record.Body);
        return builder.ToString();
    }

    /// <summary>The source_key value of a rendered file, or null when the front matter has none.</summary>
    public static string? ReadSourceKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
        {
            return null;
        }
        for (var i = 1; i < lines.Length && lines[i] != "---"; i++)
        {
            const string prefix = "source_key: ";
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return lines[i][prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>
    /// A 26-character ULID-shaped id derived from the job and source key, so a re-run gives the same id. The first 128 bits of
    /// SHA-256 are encoded in Crockford base32.
    /// </summary>
    public static string UlidFor(string jobId, string sourceKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(jobId + "\0" + sourceKey));
        var value = new BigInteger(digest.AsSpan(0, 16), isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 0; i < chars.Length; i++)
        {
            var shift = 5 * (chars.Length - 1 - i);
            var digit = (int)((value >> shift) & 31);
            chars[i] = _crockford[digit];
        }
        return new string(chars);
    }

    /// <summary>Strips characters doc 18 forbids in titles (# | ^ : %% [[ ]]). Falls back to the source key when empty.</summary>
    public static string SafeTitle(string title, string fallback)
    {
        var cleaned = title.Replace("%%", " ", StringComparison.Ordinal);
        cleaned = cleaned.Replace("[[", " ", StringComparison.Ordinal).Replace("]]", " ", StringComparison.Ordinal);
        foreach (var c in "#|^:[]")
        {
            cleaned = cleaned.Replace(c, ' ');
        }
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return cleaned.Length > 0 ? cleaned : fallback;
    }
}

/// <summary>
/// Finds Vault notes by title at HEAD. Vault files do not store the source kind, so the match is on title and the
/// returned kind echoes the wanted one. The version is the HEAD commit the note was read at.
/// </summary>
public sealed class VaultExistingObjectIndex(IGitProvider git, string repoPath) : IExistingObjectIndex
{
    public async Task<IReadOnlyList<ExistingObject>> FindAsync(IReadOnlyList<(string Kind, string Title)> wanted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (wanted.Count == 0)
        {
            return [];
        }

        var head = await git.HeadAsync(repoPath, ct);
        var byTitle = new Dictionary<string, (string Id, string Path)>(StringComparer.Ordinal);
        foreach (var path in await git.ListFilesAsync(repoPath, head, ct))
        {
            if (!path.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }
            var text = await git.ReadTextAsync(repoPath, head, path, ct);
            var front = FrontMatterOf(text);
            if (front.Title is null || front.Id is null)
            {
                continue;
            }
            byTitle.TryAdd(front.Title, (front.Id, path));
        }

        var found = new List<ExistingObject>();
        foreach (var (kind, title) in wanted)
        {
            if (byTitle.TryGetValue(title, out var match))
            {
                found.Add(new ExistingObject(kind, title, match.Id, head));
            }
        }
        return found;
    }

    /// <summary>The id and title lines of a note's leading front matter block, or nulls when there is none.</summary>
    private static (string? Id, string? Title) FrontMatterOf(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
        {
            return (null, null);
        }
        string? id = null;
        string? title = null;
        for (var i = 1; i < lines.Length && lines[i] != "---"; i++)
        {
            if (lines[i].StartsWith("id: ", StringComparison.Ordinal)) id = lines[i]["id: ".Length..].Trim();
            else if (lines[i].StartsWith("title: ", StringComparison.Ordinal)) title = lines[i]["title: ".Length..].Trim();
        }
        return (id, title);
    }
}
