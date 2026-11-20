using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>Default <see cref="IHiveGrantPolicy"/>: eligibility floors first, then the grant table.</summary>
public sealed class HiveGrantPolicy(IHiveGrantStore grants, IHiveSettings settings) : IHiveGrantPolicy
{
    public async Task<bool> MayCopyAsync(LessonRef lesson, ProjectId target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var options = await settings.GetAsync(ct).ConfigureAwait(false);
        if (HiveEligibility.Check(lesson, target, options) != HiveRefusal.None)
            return false;
        return await grants.ExistsAsync(lesson.Project, target, lesson.Kind, ct).ConfigureAwait(false);
    }
}
