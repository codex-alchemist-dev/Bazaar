using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Roadmap;

namespace Bazaar.Server.Roadmap;

/// <summary>One stored Vault document that carries roadmap front matter: its path, title and front matter as JSON text.</summary>
public sealed record RoadmapDocumentRow(string Path, string Title, string FrontMatterJson);

/// <summary>
/// The documents the read model takes, plus the documents that were left out and why. <see cref="StatesByPath"/> holds the
/// linked task states per objective path (see <see cref="RoadmapReadModel.Build"/>).
/// </summary>
public sealed record RoadmapLoad(
    IReadOnlyList<RoadmapDocumentInput> Documents,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StatesByPath,
    IReadOnlyList<RoadmapDocumentErrorDto> Errors);

/// <summary>
/// Turns stored Vault rows into read-model inputs (doc 38, "Objectives are Vault documents"). Pure: the database part is in
/// <see cref="RoadmapRepository"/>. A document that fails the front-matter contract is reported and left out, so one bad file
/// cannot hide the rest of the roadmap. A phase whose slug repeats an earlier phase is reported too; the first by path wins.
/// </summary>
public static class RoadmapDocumentLoader
{
    private const string _markdownSuffix = ".md";

    public static RoadmapLoad Classify(
        IEnumerable<RoadmapDocumentRow> rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> statesByPath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(statesByPath);

        var documents = ImmutableArray.CreateBuilder<RoadmapDocumentInput>();
        var errors = ImmutableArray.CreateBuilder<RoadmapDocumentErrorDto>();
        var phaseSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            JsonObject? frontMatter;
            try
            {
                frontMatter = JsonNode.Parse(row.FrontMatterJson) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                frontMatter = null;
            }

            if (frontMatter is null)
            {
                errors.Add(new RoadmapDocumentErrorDto(row.Path, "frontmatter", "Front matter is not a JSON object."));
                continue;
            }

            var parsed = RoadmapFrontMatter.Parse(frontMatter);
            if (!parsed.IsValid)
            {
                foreach (var error in parsed.Errors)
                    errors.Add(new RoadmapDocumentErrorDto(row.Path, error.Field, error.Message));
                continue;
            }

            switch (parsed.Document)
            {
                case RoadmapObjective:
                    documents.Add(new RoadmapDocumentInput(row.Path, row.Title, null, parsed.Document));
                    break;

                case RoadmapPhase:
                    var slug = SlugOf(row.Path);
                    if (slug is null)
                    {
                        errors.Add(new RoadmapDocumentErrorDto(row.Path, "path", "A phase document is a Markdown file; its name is the slug."));
                    }
                    else if (!phaseSlugs.Add(slug))
                    {
                        errors.Add(new RoadmapDocumentErrorDto(row.Path, "slug", $"Another phase already uses the slug '{slug}'."));
                    }
                    else
                    {
                        documents.Add(new RoadmapDocumentInput(row.Path, row.Title, slug, parsed.Document));
                    }

                    break;
            }
        }

        return new RoadmapLoad(documents.ToImmutable(), statesByPath, errors.ToImmutable());
    }

    /// <summary>The slug of a phase document: its file name without the <c>.md</c> suffix. Null when the path is not a Markdown file.</summary>
    public static string? SlugOf(string path)
    {
        if (!path.EndsWith(_markdownSuffix, StringComparison.Ordinal))
            return null;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name[..^_markdownSuffix.Length] is { Length: > 0 } slug ? slug : null;
    }
}
