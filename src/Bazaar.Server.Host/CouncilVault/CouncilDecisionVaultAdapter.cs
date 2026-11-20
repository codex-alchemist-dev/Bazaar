using Bazaar.Server.Council.Service;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Host.CouncilVault;

/// <summary>
/// Production <see cref="ICouncilDecisionWriter"/>: writes a decided vote as a Vault decision record (C24, doc 10 Recording).
/// The Council supplies the body; <see cref="VaultWriter"/> owns the front matter (id, type, title, status) and the
/// <c>decisions/NNNN-slug.md</c> number, so the adapter strips any front matter the Council wrote and passes the body only.
/// </summary>
/// <remarks>
/// The writer is taken as a delegate (in production <see cref="VaultWriter.CreateDecisionAsync"/>) so the adapter is testable
/// without a git repository. Idempotency is the writer's: a retry for the same vote id returns the number already written.
/// </remarks>
internal sealed class CouncilDecisionVaultAdapter : ICouncilDecisionWriter
{
    /// <summary>Commit identity for council decisions (the <c>Bazaar-Agent</c> trailer).</summary>
    internal const string CouncilAgentId = "council";

    private readonly Func<DecisionDraft, CancellationToken, Task<VaultEditResult>> _createDecision;

    public CouncilDecisionVaultAdapter(Func<DecisionDraft, CancellationToken, Task<VaultEditResult>> createDecision)
    {
        _createDecision = createDecision ?? throw new ArgumentNullException(nameof(createDecision));
    }

    /// <summary>Writes the decision file for <paramref name="voteId"/> and returns its NNNN.</summary>
    public async Task<int> WriteDecisionAsync(Guid voteId, string slug, string markdownBody, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownBody);

        var draft = new DecisionDraft(
            voteId,
            slug,
            $"Council decision {voteId:D}",
            StripFrontMatter(markdownBody),
            CouncilAgentId);
        var result = await _createDecision(draft, ct);
        return result.DecisionNumber
            ?? throw new InvalidOperationException($"The Vault wrote council decision {voteId:D} without a number.");
    }

    /// <summary>
    /// Removes a leading <c>---</c> front-matter block, if present, and the blank lines after it. Markdown without front matter
    /// is returned unchanged.
    /// </summary>
    internal static string StripFrontMatter(string markdown)
    {
        var lines = markdown.Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd('\r') != "---")
        {
            return markdown;
        }

        var close = Array.FindIndex(lines, 1, line => line.TrimEnd('\r') == "---");
        if (close < 0)
        {
            return markdown;
        }

        return string.Join('\n', lines, close + 1, lines.Length - close - 1).TrimStart('\r', '\n');
    }
}
