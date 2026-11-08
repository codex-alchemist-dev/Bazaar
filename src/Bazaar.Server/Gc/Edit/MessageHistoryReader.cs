using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Edit;

/// <summary>Why a stored version chain cannot be shown. Never paired with a partial list.</summary>
public enum MessageHistoryFault
{
    /// <summary>No versions were supplied.</summary>
    Empty,

    /// <summary>The versions belong to more than one message.</summary>
    MixedMessages,

    /// <summary>The versions belong to more than one GC.</summary>
    MixedGcs,

    /// <summary>Two versions carry the same version number.</summary>
    DuplicateVersion,

    /// <summary>Version numbers are not exactly 1..n.</summary>
    VersionGap,

    /// <summary>Seq does not strictly increase with Version.</summary>
    SeqNotIncreasing,
}

/// <summary>A validated history: every version of one message, oldest first.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="Versions">Versions 1..n in ascending order.</param>
/// <param name="Edited">True when there is more than one version; clients show "edited".</param>
public sealed record MessageHistory(
    Guid MessageId,
    IReadOnlyList<MessageVersionRecord> Versions,
    bool Edited);

/// <summary>Result of <see cref="MessageHistoryReader.GetHistory"/>.</summary>
public abstract record MessageHistoryResult;

/// <summary>The viewer may read the GC; the history is validated and returned.</summary>
/// <param name="History">The validated history.</param>
public sealed record MessageHistoryFound(MessageHistory History) : MessageHistoryResult;

/// <summary>The viewer may not read the GC. No version content is returned.</summary>
public sealed record MessageHistoryDenied : MessageHistoryResult;

/// <summary>The stored chain is inconsistent. No partial list is returned.</summary>
/// <param name="Fault">What is wrong with the chain.</param>
public sealed record MessageHistoryChainBroken(MessageHistoryFault Fault) : MessageHistoryResult;

/// <summary>
/// Read-side policy for message history (docs/design/01-gc-data-model.md, Edits). Pure: the caller
/// loads the version rows and supplies the GC read check.
/// </summary>
public static class MessageHistoryReader
{
    /// <summary>
    /// Validates and orders the versions of one message. The read check runs first, so a denied
    /// viewer learns nothing about the chain.
    /// </summary>
    /// <param name="versions">All stored versions of one message, in any order.</param>
    /// <param name="viewer">Who asks to read.</param>
    /// <param name="canReadGc">Read check for the GC that holds the message.</param>
    public static MessageHistoryResult GetHistory(
        IReadOnlyList<MessageVersionRecord> versions,
        GcViewer viewer,
        Func<GcViewer, bool> canReadGc)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(canReadGc);

        if (!canReadGc(viewer))
        {
            return new MessageHistoryDenied();
        }

        if (versions.Count == 0)
        {
            return new MessageHistoryChainBroken(MessageHistoryFault.Empty);
        }

        var first = versions[0];
        for (var i = 1; i < versions.Count; i++)
        {
            if (versions[i].MessageId != first.MessageId)
            {
                return new MessageHistoryChainBroken(MessageHistoryFault.MixedMessages);
            }

            if (versions[i].GcId != first.GcId)
            {
                return new MessageHistoryChainBroken(MessageHistoryFault.MixedGcs);
            }
        }

        var ordered = versions.OrderBy(v => v.Version).ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            var expected = i + 1;
            if (i > 0 && ordered[i].Version == ordered[i - 1].Version)
            {
                return new MessageHistoryChainBroken(MessageHistoryFault.DuplicateVersion);
            }

            if (ordered[i].Version != expected)
            {
                return new MessageHistoryChainBroken(MessageHistoryFault.VersionGap);
            }

            if (i > 0 && ordered[i].Seq <= ordered[i - 1].Seq)
            {
                return new MessageHistoryChainBroken(MessageHistoryFault.SeqNotIncreasing);
            }
        }

        var history = new MessageHistory(
            first.MessageId,
            ordered,
            Edited: ordered.Length > 1);

        return new MessageHistoryFound(history);
    }
}
