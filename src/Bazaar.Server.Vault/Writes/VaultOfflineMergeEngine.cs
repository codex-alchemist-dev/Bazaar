using System.Globalization;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Lands one device's offline Vault commit through the single write path (doc 56 section 5 item 8, doc 18 write path).
/// A clean merge lands on main with the offline trailers. Overlapping edits leave main untouched and keep the device's
/// commit on <c>conflict/&lt;device&gt;/&lt;timestamp&gt;</c>, so both versions survive and no edit is lost. Replaying the same
/// request is safe: a commit already on main is reported as merged, and a conflict branch that already exists is kept as is.
/// </summary>
public sealed class VaultOfflineMergeEngine : IVaultOfflineMergeEngine
{
    private readonly IVaultWriter _writer;
    private readonly IGitProvider _git;
    private readonly IGitBranchWriter _branches;
    private readonly string _repoPath;

    /// <param name="writer">The Vault write path; its lanes serialise this merge with every other write.</param>
    /// <param name="git">The git engine, for reading refs.</param>
    /// <param name="branches">Creates the conflict branch.</param>
    /// <param name="repoPath">The main checkout of the Vault repository.</param>
    public VaultOfflineMergeEngine(IVaultWriter writer, IGitProvider git, IGitBranchWriter branches, string repoPath)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _branches = branches ?? throw new ArgumentNullException(nameof(branches));
        _repoPath = repoPath ?? throw new ArgumentNullException(nameof(repoPath));
    }

    /// <inheritdoc />
    public async ValueTask<VaultOfflineMergeResult> MergeAsync(VaultOfflineMergeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string head;
        try
        {
            head = await _git.ResolveCommitAsync(_repoPath, request.Branch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VaultOfflineMergeResult.Rejected($"Branch {request.Branch} does not exist in the Vault.");
        }

        if (!string.Equals(head, request.HeadCommit, StringComparison.OrdinalIgnoreCase))
        {
            return new VaultOfflineMergeResult.Rejected($"Branch {request.Branch} does not point at the pushed commit.");
        }

        try
        {
            await _git.ResolveCommitAsync(_repoPath, request.BaseCommit, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VaultOfflineMergeResult.Rejected("The base commit does not exist in the Vault.");
        }

        var edit = new VaultEditRequest(
            request.WriteId,
            request.BaseCommit,
            request.Branch,
            $"device-{request.DeviceId:D}",
            null,
            null,
            [
                new("Bazaar-Device", request.DeviceId.ToString("D")),
                new("Bazaar-Offline-Branch", request.Branch),
                new("Bazaar-Offline-Write", request.WriteId.ToString("D")),
            ]);

        var result = await _writer.SubmitAsync(edit, ct);
        switch (result.Outcome)
        {
            case VaultEditOutcome.Merged:
            case VaultEditOutcome.UpToDate:
                return new VaultOfflineMergeResult.Merged(result.NewCommit);

            case VaultEditOutcome.Conflicted:
                var name = ConflictBranchName(request);
                await _branches.CreateBranchAsync(_repoPath, name, head, ct);
                return new VaultOfflineMergeResult.Conflicted(name, result.ConflictPaths);

            default:
                return new VaultOfflineMergeResult.Rejected(
                    "The commit changes frozen records: " + string.Join(", ", result.ConflictPaths));
        }
    }

    /// <summary>The conflict branch for a request. The write id suffix keeps two writes with the same timestamp apart.</summary>
    internal static string ConflictBranchName(VaultOfflineMergeRequest request) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"conflict/{request.DeviceId:D}/{request.At.UtcDateTime:yyyyMMddTHHmmssZ}-{request.WriteId:N}");
}
