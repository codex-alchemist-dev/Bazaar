using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Hive.Settings;

namespace Bazaar.Server.Hive;

/// <summary>
/// Applies <c>hive.allowed_kinds</c> and the grant matrix. With no grants, nothing may be copied.
/// </summary>
public sealed class HiveGrantPolicy : IHiveGrantPolicy
{
    private readonly IHiveGrantStore _grants;
    private readonly IHiveKindSource _kinds;

    /// <summary>Creates the policy.</summary>
    public HiveGrantPolicy(IHiveGrantStore grants, HiveCopyOptions? options = null, IHiveKindSource? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        _grants = grants;
        _kinds = kinds ?? new OptionsHiveKindSource(options ?? new HiveCopyOptions());
    }

    /// <summary>True when the kind is in the allowed kinds.</summary>
    public async Task<bool> IsKindAllowedAsync(HiveKind kind, CancellationToken ct) =>
        (await _kinds.AllowedKindsAsync(ct).ConfigureAwait(false)).Contains(kind);

    /// <inheritdoc />
    public async Task<bool> MayCopyAsync(LessonRef lesson, string targetProject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        ArgumentException.ThrowIfNullOrEmpty(targetProject);

        return await IsKindAllowedAsync(lesson.Kind, ct).ConfigureAwait(false)
            && await _grants.IsGrantedAsync(lesson.OriginProject, targetProject, lesson.Kind, ct).ConfigureAwait(false);
    }
}
