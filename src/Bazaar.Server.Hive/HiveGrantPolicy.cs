using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>
/// Applies <c>hive.allowed_kinds</c> and the grant matrix. With no grants, nothing may be copied.
/// </summary>
public sealed class HiveGrantPolicy : IHiveGrantPolicy
{
    private readonly IHiveGrantStore _grants;
    private readonly HiveCopyOptions _options;

    /// <summary>Creates the policy.</summary>
    public HiveGrantPolicy(IHiveGrantStore grants, HiveCopyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        _grants = grants;
        _options = options ?? new HiveCopyOptions();
    }

    /// <summary>True when the kind is in the allowed kinds.</summary>
    public bool IsKindAllowed(HiveKind kind) => _options.AllowedKinds.Contains(kind);

    /// <inheritdoc />
    public async Task<bool> MayCopyAsync(LessonRef lesson, string targetProject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        ArgumentException.ThrowIfNullOrEmpty(targetProject);

        return IsKindAllowed(lesson.Kind)
            && await _grants.IsGrantedAsync(lesson.OriginProject, targetProject, lesson.Kind, ct).ConfigureAwait(false);
    }
}
