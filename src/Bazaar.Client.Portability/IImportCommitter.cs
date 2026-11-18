namespace Bazaar.Client.Portability;

/// <summary>Result of a dry-run against a scratch target. <see cref="MappingHash"/> is the hash the dry-run covered.</summary>
public sealed record ImportDryRunReport(bool Succeeded, string MappingHash, int WouldWrite, int WouldConflict, string? Message = null);

/// <summary>Result of a real commit. Failed batches are rolled back by the committer.</summary>
public sealed record ImportCommitReport(bool Succeeded, int Written, string? Message = null);

/// <summary>
/// Host-side contract for the dry-run and commit stages (doc 44 section 8.1, stages 7 and 8).
/// The wizard state machine never calls this; the host runs it and feeds the reports back into
/// <see cref="ImportWizard"/>.
/// </summary>
public interface IImportCommitter
{
    Task<ImportDryRunReport> DryRunAsync(ImportPipelineResult plan, CancellationToken cancellationToken);

    Task<ImportCommitReport> CommitAsync(ImportPipelineResult plan, CancellationToken cancellationToken);
}
