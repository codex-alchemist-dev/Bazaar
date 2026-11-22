namespace Bazaar.Server.Souls.Judgments;

/// <summary>A decision file ready to write: the Markdown body and the slug the file name carries.</summary>
/// <param name="DecisionId">The decision reference. The writer must treat a repeat of the same id as the same file.</param>
/// <param name="Slug">The lowercase, hyphenated name part, such as <c>promote-to-reviewer</c>.</param>
/// <param name="Markdown">The complete file content, front matter included.</param>
public sealed record DecisionFile(Guid DecisionId, string Slug, string Markdown);

/// <summary>Where a decision file landed.</summary>
/// <param name="Path">The Vault-relative path, <c>decisions/NNNN-slug.md</c>.</param>
/// <param name="Number">The NNNN the writer assigned.</param>
public sealed record DecisionFileReceipt(string Path, int Number);

/// <summary>
/// Port to the Vault decision writer (docs/design/18, <c>VaultWriter</c>). The real writer is not merged yet (work package 18
/// WP4). Every decision file must go through this port: no other code writes <c>decisions/</c> directly.
/// Implementations must be idempotent by <see cref="DecisionFile.DecisionId"/>: a repeat returns the existing receipt and
/// writes nothing.
/// </summary>
public interface IDecisionFileWriter
{
    /// <summary>Writes the decision file, or returns the receipt of the file already written for this decision id.</summary>
    Task<DecisionFileReceipt> WriteAsync(DecisionFile file, CancellationToken cancellationToken);
}
