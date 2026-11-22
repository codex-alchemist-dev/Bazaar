using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>git_diff</c> tool: the worktree's changes against the repository's default branch (doc 17: git status and diff are Read).
/// No arguments. The patch is capped; <c>patch_truncated</c> says when the cap cut it. The patch is repository content and is data only.
/// </summary>
public sealed class GitDiffToolHandler(CodingGitService git, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "git_diff";

    /// <summary>Most patch characters returned to the model.</summary>
    public const int MaxPatchCharacters = 200_000;

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
        try
        {
            _ = new CodingToolArgs(toolCall.Args);
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        var worktree = GitToolSupport.Resolve(toolCall, host, out var error);
        if (worktree is null)
        {
            return error!;
        }

        GitDiff diff;
        try
        {
            diff = await git.DiffAgainstBaseAsync(worktree, cancellationToken).ConfigureAwait(false);
        }
        catch (CodingGitException ex)
        {
            return GitToolSupport.FromGit(ex);
        }

        var patch = diff.Patch.Length <= MaxPatchCharacters ? diff.Patch : diff.Patch[..MaxPatchCharacters];
        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteString("base_branch", diff.BaseBranch);
            w.WriteStartArray("changed_paths");
            foreach (var path in diff.ChangedPaths)
            {
                w.WriteStringValue(path);
            }

            w.WriteEndArray();
            w.WriteString("patch", patch);
            w.WriteBoolean("patch_truncated", patch.Length != diff.Patch.Length);
        }));
    }
}
