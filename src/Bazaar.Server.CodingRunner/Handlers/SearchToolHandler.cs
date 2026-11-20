using Bazaar.Server.CodingPermissions;
using Bazaar.Server.RepoMap;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>search</c> tool: a ripgrep search of the worktree (doc 17: Read). Arguments: <c>pattern</c> (required, 1 to 256
/// characters), <c>glob</c>, <c>max_results</c> (1 to 1000, default 100), <c>case_insensitive</c>. Matched text is repository
/// content and is returned as data only.
/// </summary>
public sealed class SearchToolHandler(ISearchTool search) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "search";

    private const int _maxPattern = 256;
    private const int _maxGlob = 200;

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Read;

    /// <inheritdoc />
    public bool WritesWorktree => false;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        SearchRequest request;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "pattern", "glob", "max_results", "case_insensitive");
            request = new SearchRequest(
                args.RequiredString("pattern", _maxPattern),
                args.OptionalString("glob", _maxGlob),
                args.OptionalInt("max_results", 1, 1000, 100),
                args.OptionalBool("case_insensitive", false));
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        SearchResult result;
        try
        {
            result = await search.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The search tool rejects patterns and globs it cannot run; the message can echo the input, so it is not passed on.
            return CodingToolResults.Failure(CodingToolResults.MalformedCode, "The search request is not valid.", CodingToolResults.MalformedExitCode);
        }
        catch (RepoToolException)
        {
            // A partial search is never reported as complete.
            return CodingToolResults.Failure("search_failed", "The search did not finish, so no result is returned.");
        }

        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteBoolean("truncated", result.Truncated);
            w.WriteStartArray("matches");
            foreach (var match in result.Matches)
            {
                w.WriteStartObject();
                w.WriteString("path", match.Path);
                w.WriteNumber("line", match.Line);
                w.WriteString("text", match.Text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));
    }
}
