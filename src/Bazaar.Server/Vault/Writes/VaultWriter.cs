using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// The single write path into one Vault repository (doc 18, "Write path", C24). Agents submit a branch they edited in
/// their own worktree; the writer merges it into the main checkout and records the commit with Bazaar trailers.
/// Decision records are created here too, so numbering stays in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Queues.</b> Submissions for one document id run one at a time. Every commit to the Vault also takes the vault
/// lane, so only one commit lands at a time per repository. Both queues are in-process; a multi-process host adds its
/// own lock above this class.
/// </para>
/// <para>
/// <b>Conflicts.</b> Overlapping edits abort the merge, keep the agent's branch, and return the conflicting paths so
/// the caller can post a conflict card. Nothing is committed and the main checkout is left clean.
/// </para>
/// <para>
/// <b>Not yet here:</b> memory batching, the conflict card itself, the indexer hook, and the decision immutability
/// check (doc 18 work packages 9 and the indexer, owned by other threads).
/// </para>
/// </remarks>
public sealed partial class VaultWriter : IDisposable
{
    private const string DecisionsFolder = "decisions";

    private readonly IGitProvider _git;
    private readonly string _repoPath;
    private readonly GitIdentity _writer;
    private readonly SemaphoreSlim _vaultLane = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _documentQueues = new();

    public VaultWriter(IGitProvider git, string repoPath, GitIdentity writer)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _repoPath = repoPath ?? throw new ArgumentNullException(nameof(repoPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <summary>
    /// Merges an agent's edit branch into the main checkout and records the commit. The result carries the head before and
    /// after, so the indexer can process <c>(OldCommit, NewCommit)</c>.
    /// </summary>
    public async Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseCommit);
        ValidateTrailerValue(request.AgentId, nameof(request.AgentId));
        ValidateTrailerValue(request.BaseCommit, nameof(request.BaseCommit));
        ValidateOptionalTrailerValue(request.GcId, nameof(request.GcId));
        ValidateOptionalTrailerValue(request.TaskId, nameof(request.TaskId));

        using var documentLease = await AcquireAsync(_documentQueues.GetOrAdd(request.DocId, _ => new SemaphoreSlim(1, 1)), ct);
        using var laneLease = await AcquireAsync(_vaultLane, ct);

        var oldCommit = await _git.HeadAsync(_repoPath, ct);
        var message = BuildMergeMessage(request);

        MergeResult merge;
        try
        {
            merge = await _git.MergeAsync(_repoPath, request.Branch, _writer, ct, message);
        }
        catch
        {
            // Never leave a half-merged checkout behind for the next writer.
            await _git.AbortMergeAsync(_repoPath, CancellationToken.None);
            throw;
        }

        if (merge.Outcome == GitMergeOutcome.Conflicted)
        {
            await _git.AbortMergeAsync(_repoPath, ct);
            return new VaultEditResult(VaultEditOutcome.Conflicted, oldCommit, oldCommit, merge.ConflictPaths, null);
        }

        var outcome = merge.Outcome == GitMergeOutcome.UpToDate ? VaultEditOutcome.UpToDate : VaultEditOutcome.Merged;
        return new VaultEditResult(outcome, oldCommit, merge.CommitSha ?? oldCommit, [], null);
    }

    /// <summary>
    /// Creates a decision record at <c>decisions/NNNN-&lt;slug&gt;.md</c> with the next free number and commits it. The
    /// number is assigned under the vault lane, so two concurrent decisions never share one.
    /// </summary>
    public async Task<VaultEditResult> CreateDecisionAsync(DecisionDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.AgentId);
        ValidateTrailerValue(draft.AgentId, nameof(draft.AgentId));
        ValidateSlug(draft.Slug);
        ValidateFrontMatterTitle(draft.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Body);

        using var documentLease = await AcquireAsync(_documentQueues.GetOrAdd(draft.DocId, _ => new SemaphoreSlim(1, 1)), ct);
        using var laneLease = await AcquireAsync(_vaultLane, ct);

        var oldCommit = await _git.HeadAsync(_repoPath, ct);
        var number = NextDecisionNumber(Path.Combine(_repoPath, DecisionsFolder));
        var relativePath = $"{DecisionsFolder}/{number:D4}-{draft.Slug}.md";
        var fullPath = Path.Combine(_repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await File.WriteAllTextAsync(fullPath, RenderDecision(draft), new UTF8Encoding(false), ct);

        var message = new StringBuilder($"Decision {number:D4}: {draft.Title}");
        message.Append("\n\n");
        AppendTrailers(message, draft.AgentId, gcId: null, taskId: null, baseCommit: oldCommit);
        var newCommit = await _git.CommitAsync(_repoPath, message.ToString(), _writer, ct);

        return new VaultEditResult(VaultEditOutcome.Merged, oldCommit, newCommit, [], number);
    }

    /// <summary>Releases the vault lane. In-flight submissions should finish before the writer is disposed.</summary>
    public void Dispose() => _vaultLane.Dispose();

    private static string BuildMergeMessage(VaultEditRequest request)
    {
        var message = new StringBuilder($"Merge {request.Branch} into vault");
        message.Append("\n\n");
        AppendTrailers(message, request.AgentId, request.GcId, request.TaskId, request.BaseCommit);
        return message.ToString();
    }

    private static void AppendTrailers(StringBuilder message, string agentId, string? gcId, string? taskId, string baseCommit)
    {
        message.Append("Bazaar-Agent: ").Append(agentId).Append('\n');
        if (gcId is not null)
        {
            message.Append("Bazaar-GC: ").Append(gcId).Append('\n');
        }

        if (taskId is not null)
        {
            message.Append("Bazaar-Task: ").Append(taskId).Append('\n');
        }

        message.Append("Bazaar-Edit-Base: ").Append(baseCommit).Append('\n');
    }

    /// <summary>Next free decision number: one more than the highest <c>NNNN-</c> prefix in the folder, starting at 1.</summary>
    internal static int NextDecisionNumber(string decisionsDirectory)
    {
        if (!Directory.Exists(decisionsDirectory))
        {
            return 1;
        }

        var highest = Directory.EnumerateFiles(decisionsDirectory, "*.md")
            .Select(path => DecisionPrefix().Match(Path.GetFileName(path)))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
        return highest + 1;
    }

    private static string RenderDecision(DecisionDraft draft)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(draft.DocId.ToString("D")).Append('\n');
        builder.Append("type: decision\n");
        builder.Append("title: ").Append(draft.Title).Append('\n');
        builder.Append("status: proposed\n");
        builder.Append("---\n\n");
        builder.Append(draft.Body.TrimEnd()).Append('\n');
        return builder.ToString();
    }

    private static void ValidateSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (!SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException("Slug must be lowercase letters, digits and single hyphens.", nameof(slug));
        }
    }

    /// <summary>Doc 18 forbids these characters in front-matter titles: # | ^ : %% [[ ]].</summary>
    private static void ValidateFrontMatterTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.IndexOfAny(['#', '|', '^', ':']) >= 0 || title.Contains("%%", StringComparison.Ordinal)
            || title.Contains("[[", StringComparison.Ordinal) || title.Contains("]]", StringComparison.Ordinal))
        {
            throw new ArgumentException("Title contains a character that front matter forbids (# | ^ : %% [[ ]]).", nameof(title));
        }
    }

    private static void ValidateTrailerValue(string value, string paramName)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("Trailer values must be single-line.", paramName);
        }
    }

    private static void ValidateOptionalTrailerValue(string? value, string paramName)
    {
        if (value is not null)
        {
            ValidateTrailerValue(value, paramName);
        }
    }

    private static async Task<IDisposable> AcquireAsync(SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    [GeneratedRegex("^(?<number>\\d{4})-", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionPrefix();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

public enum VaultEditOutcome
{
    /// <summary>The branch was merged and a merge commit recorded.</summary>
    Merged,

    /// <summary>The branch was already contained in the main checkout; nothing changed.</summary>
    UpToDate,

    /// <summary>Overlapping edits. Nothing was committed; the branch is kept for the agent to rebase.</summary>
    Conflicted,
}

/// <summary>An agent's request to land its edit branch.</summary>
/// <param name="DocId">The document the edit is about (the per-document queue key).</param>
/// <param name="BaseCommit">The commit the agent started from (<c>Bazaar-Edit-Base</c> trailer).</param>
/// <param name="Branch">The agent's <c>agent/&lt;task&gt;</c> branch.</param>
/// <param name="AgentId">The agent's identity (<c>Bazaar-Agent</c> trailer).</param>
/// <param name="GcId">The GC the edit belongs to, or null for memory writes (<c>Bazaar-GC</c> trailer).</param>
/// <param name="TaskId">The task, or null (<c>Bazaar-Task</c> trailer).</param>
public sealed record VaultEditRequest(
    Guid DocId,
    string BaseCommit,
    string Branch,
    string AgentId,
    string? GcId,
    string? TaskId);

/// <summary>A new decision record. The writer assigns the number and the file path.</summary>
public sealed record DecisionDraft(Guid DocId, string Slug, string Title, string Body, string AgentId);

/// <summary>
/// What a write did. <see cref="OldCommit"/> and <see cref="NewCommit"/> feed the indexer. <see cref="ConflictPaths"/> is
/// filled only when conflicted. <see cref="DecisionNumber"/> is set for decision records.
/// </summary>
public sealed record VaultEditResult(
    VaultEditOutcome Outcome,
    string OldCommit,
    string NewCommit,
    IReadOnlyList<string> ConflictPaths,
    int? DecisionNumber);
