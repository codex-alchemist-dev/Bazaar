namespace Bazaar.RyuseiFuda.Kanban;

/// <summary>A board as the directory lists it: the project id and its display name.</summary>
public sealed record BoardListing(Guid BoardId, string Name);

/// <summary>
/// Read-only list of Kanban boards (docs/design/12, "Board names"; docs/design/24, Org directory). One board is one project.
/// Lists boards that have a name row or at least one task that is not withdrawn. A board without a name row is named
/// <c>Project &lt;first 8 hex digits of the id&gt;</c>.
/// </summary>
public interface IBoardDirectoryReader
{
    Task<IReadOnlyList<BoardListing>> ListBoardsAsync(CancellationToken ct = default);
}
