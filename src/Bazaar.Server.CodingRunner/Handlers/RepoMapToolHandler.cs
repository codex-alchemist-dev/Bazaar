using Bazaar.Server.CodingPermissions;
using Bazaar.Server.RepoMap;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>repo_map</c> tool: a token-bounded outline of the symbols under a directory (doc 17: Read). Arguments: <c>root</c>
/// (default "."), <c>token_budget</c> (1 to 16000, default 1000) and <c>focus_files</c>, listed first in the order given.
/// </summary>
public sealed class RepoMapToolHandler(IRepoMapBuilder builder) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "repo_map";

    private const int _maxRoot = 1024;
    private const int _maxTokenBudget = 16_000;
    private const int _maxFocusFiles = 256;
    private const int _maxFocusPath = 1024;

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

        RepoMapRequest request;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "root", "token_budget", "focus_files");
            request = new RepoMapRequest(
                args.OptionalString("root", _maxRoot) ?? ".",
                args.OptionalInt("token_budget", 1, _maxTokenBudget, 1000),
                args.OptionalStringArray("focus_files", _maxFocusFiles, _maxFocusPath));
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        RepoMapResult map;
        try
        {
            map = await builder.BuildAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return CodingToolResults.Failure(CodingToolResults.MalformedCode, "The repo map request is not valid.", CodingToolResults.MalformedExitCode);
        }
        catch (RepoToolException)
        {
            return CodingToolResults.Failure("repo_map_failed", "The file listing did not finish, so no map is returned.");
        }

        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteString("text", map.Text);
            w.WriteNumber("included_files", map.IncludedFiles);
            w.WriteNumber("omitted_files", map.OmittedFiles);
            w.WriteNumber("included_symbols", map.IncludedSymbols);
            w.WriteNumber("omitted_symbols", map.OmittedSymbols);
            w.WriteNumber("token_estimate", map.TokenEstimate);
            w.WriteBoolean("truncated", map.Truncated);
        }));
    }
}
