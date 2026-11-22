using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Judgments;

/// <summary>Where a recorded judgment landed: the decision file and the GC message.</summary>
/// <param name="DecisionId">The decision reference both records carry.</param>
/// <param name="DecisionFilePath">The Vault path the decision file was written to.</param>
/// <param name="DecisionFileNumber">The NNNN the Vault writer assigned.</param>
/// <param name="GcSeq">The GC sequence number of the judgment message.</param>
/// <param name="GcDuplicate">True when the message had already been posted by an earlier attempt.</param>
public sealed record JudgmentReceipt(
    Guid DecisionId,
    string DecisionFilePath,
    int DecisionFileNumber,
    long GcSeq,
    bool GcDuplicate);

/// <summary>
/// Records a judgment about a Soul (docs/design/07-soul-system.md, "Auditable judgments", work package 4). It validates the
/// record, writes the decision file through <see cref="IDecisionFileWriter"/>, then posts the GC message through
/// <see cref="IGcJudgmentPoster"/> with the file's path in the text.
/// </summary>
/// <remarks>
/// The file is written first so the message can name it. If the message post fails, the file stays and the exception
/// propagates. Retry with the same <see cref="DecisionRecord.DecisionId"/>: the file writer returns the same file, and the
/// GC post is idempotent by that id, so the retry completes the judgment without a second file or message.
/// </remarks>
public sealed class JudgmentWriter(IDecisionFileWriter decisionFiles, IGcJudgmentPoster gcPoster)
{
    /// <summary>Validates and records the judgment. Throws <see cref="ArgumentException"/> for an invalid record.</summary>
    public async Task<JudgmentReceipt> RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        var file = JudgmentDecisionFile.Build(record);
        var written = await decisionFiles.WriteAsync(file, cancellationToken).ConfigureAwait(false);

        var post = await gcPoster.PostAsync(
            new JudgmentPost(record.GcId, record.AgentId, record.DecisionId, MessageText(record, written.Path)),
            cancellationToken).ConfigureAwait(false);

        return new JudgmentReceipt(record.DecisionId, written.Path, written.Number, post.Seq, post.Duplicate);
    }

    /// <summary>The GC message body: the choice, the reasoning, the Soul version and the decision file.</summary>
    private static string MessageText(DecisionRecord record, string decisionFilePath) =>
        $"Judgment on {record.Kind.ToString().ToLowerInvariant()} for Soul {record.SoulId:D} (soul version {record.SoulVersion}, "
        + $"decision {record.DecisionId:D}): {record.Title.Trim()}. Chose \"{record.Chosen.Trim()}\". "
        + $"Reasoning: {record.Reasoning.Trim()}\nDecision file: {decisionFilePath}";

    private static void Validate(DecisionRecord record)
    {
        if (record.DecisionId == Guid.Empty)
            throw new ArgumentException("A decision id is required.", nameof(record));
        if (record.SoulId == Guid.Empty)
            throw new ArgumentException("A Soul id is required.", nameof(record));
        if (record.GcId == Guid.Empty)
            throw new ArgumentException("A GC id is required.", nameof(record));
        if (record.AgentId == Guid.Empty)
            throw new ArgumentException("The deciding agent id is required.", nameof(record));
        if (record.SoulVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(record), record.SoulVersion, "A judgment runs under Soul version 1 or later.");
        if (!Enum.IsDefined(record.Kind))
            throw new ArgumentException("The judgment kind is not recognised.", nameof(record));
        if (!Enum.IsDefined(record.Confidence))
            throw new ArgumentException("The confidence is not recognised.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Title))
            throw new ArgumentException("A title is required.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Reasoning))
            throw new ArgumentException("Reasoning is required for every judgment.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Chosen))
            throw new ArgumentException("The chosen option is required.", nameof(record));
        if (record.Options.Count == 0)
            throw new ArgumentException("At least one option is required.", nameof(record));
        if (record.Options.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Options cannot be blank.", nameof(record));
        if (!record.Options.Any(option => string.Equals(option.Trim(), record.Chosen.Trim(), StringComparison.Ordinal)))
            throw new ArgumentException("The chosen option must be one of the options considered.", nameof(record));
        if (record.Inputs.Any(input => input.Seq < 1))
            throw new ArgumentException("Input sequence numbers start at 1.", nameof(record));
    }
}
