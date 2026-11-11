using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Kanban.Wire;

/// <summary>
/// Decides who may read a project's Kanban board over the wire (doc 12, board view). The board is a project-wide view, so
/// the decision is made per project, not per GC.
/// </summary>
public interface IBoardAccessPolicy
{
    /// <summary>True when <paramref name="principal"/> may read the board of <paramref name="projectId"/>.</summary>
    ValueTask<bool> CanReadAsync(PrincipalRef principal, Guid projectId, CancellationToken cancellationToken);
}

/// <summary>
/// The default board policy: only the owner principal reads a board. No table links people or agents to a project yet
/// (the membership question is open), so the safe default is the owner alone. It denies everyone else, including agents
/// and humans who are members of the project's GCs. Widening it needs a membership source and a decision, not a flag.
/// </summary>
public sealed class OwnerOnlyBoardAccess : IBoardAccessPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> CanReadAsync(PrincipalRef principal, Guid projectId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(string.Equals(principal.Kind, "owner", StringComparison.Ordinal));
}
