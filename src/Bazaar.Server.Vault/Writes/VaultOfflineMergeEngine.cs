using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// The Vault's offline push merge (doc 18, "Offline merge"; doc 49 section 4 item 2). Merges a device's pushed head into
/// the main branch: a fast-forward when main has not moved past the push base, otherwise a three-way merge in a scratch
/// worktree. Overlapping hunks leave main untouched and keep the pushed head on a <c>conflict/&lt;device&gt;/&lt;timestamp&gt;</c>
/// branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locks.</b> The whole merge runs inside the writer's vault lane, so it never interleaves with a writer submission or
/// another offline merge on this repository. Git calls take the per-checkout locks of <see cref="CheckoutLocks"/>. The
/// main checkout only ever fast-forwards; the three-way merge itself runs in the scratch worktree.
/// </para>
/// <para>
/// <b>Merge machinery.</b> The file merge is libgit2's (git's own <c>xdiff</c> three-way merge), not a hand-written diff3:
/// it auto-merges non-overlapping hunks, treats a two-sided change to a binary file as a whole-file conflict, and detects
/// renames. Its merge ancestor is git's merge base of the two heads, which is the push base or a later common commit.
/// </para>
/// <para>
/// Review items are not created here: the host adapts <see cref="IVaultOfflineMergeEngine"/> to Offline Sync and opens the
/// review item for a <see cref="VaultOfflineMergeResult.Conflicted"/> result.
/// </para>
/// </remarks>
public sealed partial class VaultOfflineMergeEngine : IVaultOfflineMergeEngine
{
    private const int _maxDeviceSegment = 64;
    private const int _maxWriteId = 100;

    private readonly VaultWriter _writer;
    private readonly string _worktreeRoot;
    private readonly TimeProvider _time;

    /// <param name="writer">The repository's writer; its lane, identity and commit observer are reused.</param>
    /// <param name="worktreeRoot">
    /// Where scratch worktrees are created. Must be outside the main checkout, or a merge would commit them.
    /// </param>
    /// <param name="time">Clock for conflict branch names; the system clock when null.</param>
    public VaultOfflineMergeEngine(VaultWriter writer, string worktreeRoot, TimeProvider? time = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var root = CheckoutLocks.Normalize(worktreeRoot);
        var repo = CheckoutLocks.Normalize(writer.RepoPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(root, repo, comparison) || root.StartsWith(repo + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("The worktree root must be outside the main checkout.", nameof(worktreeRoot));
        }

        _worktreeRoot = root;
        _time = time ?? TimeProvider.System;
    }

    public Task<VaultOfflineMergeResult> MergeAsync(VaultOfflineMergeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Request fields come from a device: malformed values are a Rejected outcome, not an exception.
        if (string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Any(char.IsControl))
        {
            return Task.FromResult<VaultOfflineMergeResult>(Reject("device id is empty or contains control characters"));
        }

        if (!OfflineMergeGit.IsValidSourceBranch(request.Branch))
        {
            return Task.FromResult<VaultOfflineMergeResult>(Reject("source branch is not a valid branch name"));
        }

        if (request.WriteId is not null
            && (request.WriteId.Length is 0 or > _maxWriteId || request.WriteId.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
        {
            return Task.FromResult<VaultOfflineMergeResult>(Reject("write id is empty, too long or contains whitespace"));
        }

        if (!IsSha(request.HeadCommit) || !IsSha(request.BaseCommit))
        {
            return Task.FromResult<VaultOfflineMergeResult>(Reject("head and base must be full commit SHAs"));
        }

        var normalized = request with
        {
            HeadCommit = request.HeadCommit.ToLowerInvariant(),
            BaseCommit = request.BaseCommit.ToLowerInvariant(),
        };
        return _writer.InVaultLaneAsync(() => MergeInLaneAsync(normalized, ct), ct);
    }

    private async Task<VaultOfflineMergeResult> MergeInLaneAsync(VaultOfflineMergeRequest request, CancellationToken ct)
    {
        var repoPath = _writer.RepoPath;
        var head = request.HeadCommit;
        var baseCommit = request.BaseCommit;

        // The target is always the main checkout's branch; the request's branch is the device's source branch.
        var mainBranch = await OfflineMergeGit.CurrentBranchAsync(repoPath, ct);
        if (mainBranch is null)
        {
            return Reject("the Vault's main checkout has no branch checked out");
        }

        if (!await OfflineMergeGit.CommitExistsAsync(repoPath, head, ct))
        {
            return Reject($"unknown head commit {head}");
        }

        if (!await OfflineMergeGit.CommitExistsAsync(repoPath, baseCommit, ct))
        {
            return Reject($"unknown base commit {baseCommit}");
        }

        // When the pushed source branch exists here, the head must be on it; otherwise the head SHA alone counts. A
        // source named like the target (the device's local "main") is the device's own branch, not the server's.
        var sourceName = OfflineMergeGit.ShortBranchName(request.Branch);
        var sourceTip = string.Equals(sourceName, mainBranch, StringComparison.Ordinal)
            ? null
            : await OfflineMergeGit.BranchTipAsync(repoPath, sourceName, ct);
        if (sourceTip is not null && !await OfflineMergeGit.IsAncestorAsync(repoPath, head, sourceTip, ct))
        {
            return Reject($"head is not reachable from source branch '{sourceName}'");
        }

        var oldMain = await _writer.Git.HeadAsync(repoPath, ct);

        // Idempotent repeat: the head is already part of main.
        if (await OfflineMergeGit.IsAncestorAsync(repoPath, head, oldMain, ct))
        {
            return new VaultOfflineMergeResult.Merged(oldMain);
        }

        if (!await OfflineMergeGit.IsAncestorAsync(repoPath, baseCommit, head, ct))
        {
            return Reject("base is not an ancestor of head");
        }

        if (!await OfflineMergeGit.IsAncestorAsync(repoPath, baseCommit, oldMain, ct))
        {
            return Reject($"base is not on branch '{mainBranch}'");
        }

        var unsafeChanges = await OfflineMergeGit.FindUnsafeChangesAsync(repoPath, baseCommit, head, ct);
        if (unsafeChanges.Count > 0)
        {
            return Reject("refused entries: " + string.Join(", ", unsafeChanges));
        }

        // The same check a writer submission gets, on the push's own changes.
        var frozen = await DecisionImmutability.FindViolationsAsync(_writer.Git, repoPath, baseCommit, head, ct);
        if (frozen.Count > 0)
        {
            return FrozenDecisions(frozen);
        }

        if (await OfflineMergeGit.IsAncestorAsync(repoPath, oldMain, head, ct))
        {
            // Main may have frozen a decision between base and its current head; check the effect on main too.
            frozen = await DecisionImmutability.FindViolationsAsync(_writer.Git, repoPath, oldMain, head, ct);
            if (frozen.Count > 0)
            {
                return FrozenDecisions(frozen);
            }

            await OfflineMergeGit.FastForwardAsync(repoPath, head, _writer.Identity, ct);
            await _writer.NotifyCommitAsync(oldMain, head, ct);
            return new VaultOfflineMergeResult.Merged(head);
        }

        return await ThreeWayMergeAsync(request, oldMain, ct);
    }

    private async Task<VaultOfflineMergeResult> ThreeWayMergeAsync(VaultOfflineMergeRequest request, string oldMain, CancellationToken ct)
    {
        var repoPath = _writer.RepoPath;
        var id = Guid.NewGuid().ToString("N");
        var scratchBranch = $"offline-merge/{id}";
        var worktreePath = Path.Combine(_worktreeRoot, $"offline-{id}");
        Directory.CreateDirectory(_worktreeRoot);

        try
        {
            // The scratch branch starts at HEAD of the main checkout, which is oldMain inside the lane.
            await _writer.Git.AddWorktreeAsync(repoPath, scratchBranch, worktreePath, ct);
            var merge = await OfflineMergeGit.MergeCommitAsync(
                worktreePath, request.HeadCommit, _writer.Identity, BuildMessage(request), ct);

            if (merge.CommitSha is null)
            {
                var prefix = $"conflict/{SanitizeDevice(request.DeviceId)}/";
                var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                var branch = await OfflineMergeGit.CreateConflictBranchAsync(repoPath, prefix, stamp, request.HeadCommit, ct);
                return new VaultOfflineMergeResult.Conflicted(branch, merge.ConflictPaths);
            }

            // An auto-merge must not edit a decision that was frozen on main after the push base.
            var frozen = await DecisionImmutability.FindViolationsAsync(_writer.Git, repoPath, oldMain, merge.CommitSha, ct);
            if (frozen.Count > 0)
            {
                return FrozenDecisions(frozen);
            }

            var current = await _writer.Git.HeadAsync(repoPath, ct);
            if (!string.Equals(current, oldMain, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The main branch moved during an offline merge; another process writes to the Vault without the host lock.");
            }

            await OfflineMergeGit.FastForwardAsync(repoPath, merge.CommitSha, _writer.Identity, ct);
            await _writer.NotifyCommitAsync(oldMain, merge.CommitSha, ct);
            return new VaultOfflineMergeResult.Merged(merge.CommitSha);
        }
        finally
        {
            await OfflineMergeGit.RemoveWorktreeAsync(repoPath, worktreePath, scratchBranch, CancellationToken.None);
        }
    }

    /// <summary>
    /// The device id as one branch-name segment: <c>[A-Za-z0-9._-]</c> only (others become '-', runs collapse), no leading
    /// '.' or '-', no "..", no ".lock" suffix, at most 64 characters, "device" when nothing is left.
    /// </summary>
    internal static string SanitizeDevice(string deviceId)
    {
        var builder = new StringBuilder(deviceId.Length);
        foreach (var c in deviceId)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        var text = Dashes().Replace(builder.ToString(), "-");
        text = Dots().Replace(text, ".");
        text = text.TrimStart('.', '-');
        if (text.Length > _maxDeviceSegment)
        {
            text = text[.._maxDeviceSegment];
        }

        while (text.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || text.EndsWith('.') || text.EndsWith('-'))
        {
            text = text.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ? text[..^".lock".Length] : text[..^1];
        }

        return text.Length == 0 ? "device" : text;
    }

    private static string BuildMessage(VaultOfflineMergeRequest request)
    {
        var device = Printable(request.DeviceId);
        var source = OfflineMergeGit.ShortBranchName(request.Branch);
        var message = new StringBuilder($"Merge offline push {source} from device {device} into vault");
        message.Append("\n\n");
        message.Append("Bazaar-Device: ").Append(device).Append('\n');
        message.Append("Bazaar-Offline-Branch: ").Append(source).Append('\n');
        if (request.WriteId is not null)
        {
            message.Append("Bazaar-Offline-Write: ").Append(request.WriteId).Append('\n');
        }
        message.Append("Bazaar-Edit-Base: ").Append(request.BaseCommit).Append('\n');
        return message.ToString();
    }

    private static VaultOfflineMergeResult.Rejected FrozenDecisions(IReadOnlyList<string> paths) =>
        Reject("changes frozen decision records: " + string.Join(", ", paths));

    private static VaultOfflineMergeResult.Rejected Reject(string reason) => new(reason);

    private static bool IsSha(string? value) => value is not null && Sha().IsMatch(value);

    private static string Printable(string value) => new([.. value.Select(c => char.IsControl(c) ? '?' : c)]);

    [GeneratedRegex("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Dashes();

    [GeneratedRegex("\\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Dots();
}
