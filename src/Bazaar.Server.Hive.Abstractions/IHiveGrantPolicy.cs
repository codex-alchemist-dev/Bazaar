namespace Bazaar.Server.Hive.Abstractions;

/// <summary>Doc 25 <c>IHiveGrantPolicy</c>: may this lesson be copied into the target project right now.</summary>
public interface IHiveGrantPolicy
{
    /// <summary>
    /// True only when the lesson is eligible (allowed kind, not an excluded class) and a grant exists from its project to
    /// <paramref name="target"/> for its kind. No grant, no copy.
    /// </summary>
    Task<bool> MayCopyAsync(LessonRef lesson, ProjectId target, CancellationToken ct);
}
