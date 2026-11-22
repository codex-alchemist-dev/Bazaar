namespace Bazaar.Server.Hive.Abstractions;

/// <summary>Decides whether a lesson may be copied for a target project (doc 25, "Data and interfaces").</summary>
public interface IHiveGrantPolicy
{
    /// <summary>True when the kind is allowed and a grant covers the origin project, target project and kind.</summary>
    Task<bool> MayCopyAsync(LessonRef lesson, string targetProject, CancellationToken ct);
}
