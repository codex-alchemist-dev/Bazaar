namespace Bazaar.Server.Kanban;

/// <summary>Checks that a GC exists, for GC links on a task (doc 12, Links). Tests replace the SQL default with a fake.</summary>
public interface IGcExistence
{
    /// <summary>True when a GC row with this id exists. Archived GCs still exist.</summary>
    Task<bool> ExistsAsync(Guid gcId, CancellationToken ct = default);
}
