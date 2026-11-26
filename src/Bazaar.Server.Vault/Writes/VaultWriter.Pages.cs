using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Page writes (doc 18, "Page writes"): one file at a path under an operator-allowed prefix, committed under the vault lane
/// like decisions. For modules that own a Vault area. Kept in its own file so it does not touch the main write path.
/// </summary>
public sealed partial class VaultWriter : IVaultPageWriter
{
    private const int _defaultMaxPageBytes = 1_048_576;

    /// <summary>
    /// The path prefixes page writes may use. Default <c>company/memory/</c> (<c>vault.page_write_prefixes</c>). Each must end
    /// with '/', and <c>decisions/</c> is refused whatever is set here.
    /// </summary>
    public IReadOnlyList<string> PageWritePrefixes { get; init; } = ["company/memory/"];

    /// <summary>The largest page in bytes (<c>vault.max_document_bytes</c>, default 1 MiB).</summary>
    public int MaxPageBytes { get; init; } = _defaultMaxPageBytes;

    /// <inheritdoc />
    public async Task<VaultPageResult> CreateOrUpdatePageAsync(VaultPageDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.AgentId);
        ValidateTrailerValue(draft.AgentId, nameof(draft.AgentId));
        ValidateOptionalTrailerValue(draft.GcId, nameof(draft.GcId));
        ValidateVaultPath(draft.Path, nameof(draft.Path));
        ValidateFrontMatterTitle(draft.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Body);
        if (!VaultFrontMatter.KnownTypes.Contains(draft.Type) || draft.Type == "decision")
        {
            throw new ArgumentException("The type must be a known document type other than decision.", nameof(draft));
        }

        var prefix = PageWritePrefixes.FirstOrDefault(p => p.EndsWith('/') && !p.StartsWith("decisions/", StringComparison.Ordinal)
            && draft.Path.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
        {
            throw new ArgumentException($"Pages may be written only under: {string.Join(", ", PageWritePrefixes)}.", nameof(draft));
        }

        var content = RenderPage(draft);
        if (Encoding.UTF8.GetByteCount(content) > MaxPageBytes)
        {
            throw new ArgumentException($"The page is larger than {MaxPageBytes} bytes.", nameof(draft));
        }

        using var documentLease = await AcquireAsync(_documentQueues.GetOrAdd(draft.DocId, _ => new SemaphoreSlim(1, 1)), ct);
        using var laneLease = await AcquireAsync(_vaultLane, ct);

        var oldCommit = await _git.HeadAsync(_repoPath, ct);
        var ulid = ToUlid(draft.DocId);
        var fullPath = ResolveInside(_repoPath, draft.Path);

        // One id, one file: the id may not already sit at another path under the page area.
        foreach (var other in EnumeratePageFiles(prefix).Where(f => !string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(await ReadIdAsync(other, ct), ulid, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This document id is already used by another page.");
            }
        }

        var outcome = VaultPageOutcome.Created;
        if (File.Exists(fullPath))
        {
            var existing = await File.ReadAllTextAsync(fullPath, ct);
            if (!string.Equals(IdOf(existing), ulid, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A different document already lives at this path.");
            }

            if (string.Equals(existing.Replace("\r\n", "\n", StringComparison.Ordinal), content, StringComparison.Ordinal))
            {
                return new VaultPageResult(VaultPageOutcome.UpToDate, oldCommit, oldCommit);
            }

            outcome = VaultPageOutcome.Updated;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), ct);

        var message = new StringBuilder(outcome == VaultPageOutcome.Created ? "Page " : "Update page ").Append(draft.Path);
        message.Append("\n\n");
        AppendTrailers(message, draft.AgentId, draft.GcId, taskId: null, baseCommit: oldCommit);
        var newCommit = await _git.CommitAsync(_repoPath, message.ToString(), _writer, ct);
        await NotifyAsync(oldCommit, newCommit, ct);
        return new VaultPageResult(outcome, oldCommit, newCommit);
    }

    private IEnumerable<string> EnumeratePageFiles(string prefix)
    {
        // Normalised like ResolveInside's result: on Windows Path.Combine keeps the prefix's '/' and the file would not match itself.
        var directory = Path.GetFullPath(Path.Combine(_repoPath, prefix.TrimEnd('/')));
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f))
            : [];
    }

    private static async Task<string?> ReadIdAsync(string file, CancellationToken ct) => IdOf(await File.ReadAllTextAsync(file, ct));

    /// <summary>The <c>id:</c> value of a page's front matter, or null.</summary>
    private static string? IdOf(string text)
    {
        var match = PageIdLine().Match(text);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string RenderPage(VaultPageDraft draft)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(ToUlid(draft.DocId)).Append('\n');
        builder.Append("type: ").Append(draft.Type).Append('\n');
        builder.Append("title: ").Append(draft.Title).Append('\n');
        builder.Append("---\n\n");
        builder.Append(draft.Body.TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        return builder.ToString();
    }

    [GeneratedRegex("^id: (?<value>[0-9A-Za-z]{26})\\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PageIdLine();
}
