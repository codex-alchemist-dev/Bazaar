using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Setup;

/// <summary>
/// An <see cref="ITemplateCatalog"/> over plain git repositories (doc 45 section 1.8). Each entry pins a URL, a full commit id, a
/// subfolder and the template hash. A lookup is exact on id and version. It fetches the pinned commit through an
/// <see cref="IGitFetcher"/> into a cache folder keyed by URL and commit, and returns a folder source over the template's subfolder.
/// The resolver then loads the template and refuses it when its hash is not the pinned one (<see cref="IPinnedTemplateCatalog"/>).
/// </summary>
/// <remarks>
/// A cached checkout is reused only when its completion marker exists, and the hash check runs on every resolve, so a tampered cache
/// is refused. The marker is written after a successful fetch. Concurrent lookups of the same commit fetch into their own temporary
/// folder and the first to finish wins. No secret is held: entries have no credentials, and private repositories rely on the
/// server's own git or ssh setup.
/// </remarks>
public sealed class GitTemplateCatalog : IPinnedTemplateCatalog
{
    private const string _marker = ".bazaar-fetch-ok";

    private readonly Dictionary<(string Id, string Version), GitTemplateEntry> _entries;
    private readonly IGitFetcher _fetcher;
    private readonly GitTemplateOptions _options;

    /// <param name="entries">The listed versions. Each is checked again here; an invalid or repeated entry is an error.</param>
    /// <param name="fetcher">Fetches one pinned commit.</param>
    /// <param name="options">The operator's git choices.</param>
    public GitTemplateCatalog(IEnumerable<GitTemplateEntry> entries, IGitFetcher fetcher, GitTemplateOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _entries = [];
        var issues = new List<SetupValidationIssue>();
        var index = 0;
        foreach (var entry in entries)
        {
            GitTemplateCatalogParser.CheckEntry(entry, $"templates[{index}]", options.AllowedSchemes, issues);
            if (!_entries.TryAdd((entry.Id, entry.Version), entry))
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, $"templates[{index}]", "repeats an id and version listed earlier."));
            }

            index++;
        }

        if (issues.Count > 0)
        {
            throw new ArgumentException("The git template catalog has an invalid entry at " + issues[0].Path + ": " + issues[0].Message, nameof(entries));
        }
    }

    /// <inheritdoc />
    public string? GetPinnedHash(string templateId, string version) =>
        _entries.TryGetValue((templateId, version), out var entry) ? entry.TemplateHash : null;

    /// <inheritdoc />
    public async Task<ITemplateSource?> FindAsync(string templateId, string version, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue((templateId, version), out var entry))
        {
            return null;
        }

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Url + "\n" + entry.Commit)))[..32];
        var checkout = Path.Combine(Path.GetFullPath(_options.CacheRoot), key);
        if (!File.Exists(Path.Combine(checkout, _marker)))
        {
            await FetchIntoAsync(entry, checkout, cancellationToken);
        }

        var root = entry.Path.Length == 0 ? checkout : Path.Combine(checkout, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
        {
            throw new TemplateSourceException("the pinned commit has no template folder at the listed path.");
        }

        return new FileSystemTemplateSource(root);
    }

    private async Task FetchIntoAsync(GitTemplateEntry entry, string checkout, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(checkout)!;
        Directory.CreateDirectory(parent);
        var temporary = checkout + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var result = await _fetcher.FetchAsync(new GitFetchRequest(entry.Url, entry.Commit, temporary), cancellationToken);
            if (!result.Succeeded)
            {
                throw new TemplateSourceException("could not fetch the template from git: " + (result.Detail ?? "unknown error"));
            }

            await File.WriteAllTextAsync(Path.Combine(temporary, _marker), string.Empty, cancellationToken);
            try
            {
                Directory.Move(temporary, checkout);
            }
            catch (IOException) when (File.Exists(Path.Combine(checkout, _marker)))
            {
                // A concurrent lookup finished first. Its checkout is the same commit.
            }
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }
}
