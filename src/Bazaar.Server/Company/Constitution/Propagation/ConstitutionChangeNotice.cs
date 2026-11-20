// TODO(component): move to the Company component project when PR #125 merges.
using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Company.Constitution.Propagation;

/// <summary>The result of propagating one <see cref="ConstitutionChanged"/> event. Rows and posts are the caller's job.</summary>
/// <param name="InboxItems">One low-priority <see cref="InboxKind.Constitution"/> item per active agent.</param>
/// <param name="GcText">The plain-text notice for the General Bazaar GC.</param>
public sealed record ConstitutionChangeResult(IReadOnlyList<InboxCandidate> InboxItems, string GcText);

/// <summary>
/// Builds the propagation output of a Constitution change (doc 24, section 1). Pure: no storage, clock or randomness.
/// The item ids come from <c>newItemId</c>, so the caller controls them.
/// </summary>
public static class ConstitutionChangeNotice
{
    /// <summary>
    /// Builds one non-waking inbox candidate per distinct active agent and the General Bazaar GC text.
    /// </summary>
    /// <param name="change">The event.</param>
    /// <param name="activeAgentIds">The active agents; duplicates are ignored.</param>
    /// <param name="dueAt">When the inbox items become due.</param>
    /// <param name="newItemId">Produces one id per inbox item.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static ConstitutionChangeResult Build(
        ConstitutionChanged change,
        IEnumerable<Guid> activeAgentIds,
        DateTimeOffset dueAt,
        Func<Guid> newItemId)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(activeAgentIds);
        ArgumentNullException.ThrowIfNull(newItemId);

        var items = activeAgentIds
            .Distinct()
            .Select(agentId => new InboxCandidate(newItemId(), agentId, InboxKind.Constitution, null, null, dueAt))
            .ToList();

        return new ConstitutionChangeResult(items, BuildGcText(change));
    }

    private static string BuildGcText(ConstitutionChanged change)
    {
        var from = change.From ?? "none (first activation)";
        var sections = string.Join(", ", change.Sections);

        return $"Constitution updated. From: {from}. To: {change.To}. Changed sections: {sections}.";
    }
}
