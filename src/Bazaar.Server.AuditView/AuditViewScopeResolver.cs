using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.AuditView;

/// <summary>
/// Resolves the scope from the viewer's roles through <see cref="IPermissionChecker"/>. Only a human can hold a scope: agents
/// and services get none (doc 48 section 5.4). The owner capability gives every category; otherwise each category needs the
/// capability <c>audit.view.&lt;category&gt;</c>. Any failure to check yields no access.
/// </summary>
public sealed class AuditViewScopeResolver(IPermissionChecker checker, AuditViewOptions options) : IAuditViewScopeResolver
{
    private readonly IPermissionChecker _checker = checker ?? throw new ArgumentNullException(nameof(checker));
    private readonly AuditViewOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<AuditViewScope> ResolveAsync(AuditViewer viewer, CancellationToken ct = default)
    {
        if (viewer.Kind != PrincipalKind.Human || viewer.PrincipalId == Guid.Empty)
            return AuditViewScope.Denied;

        try
        {
            if (await _checker.IsAllowedAsync(viewer.PrincipalId, viewer.Kind, new Capability(_options.OwnerCapability), Scope.Instance, ct)
                    .ConfigureAwait(false))
                return AuditViewScope.Everything;

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in _options.CategoryMap.Keys)
            {
                var capability = new Capability(_options.CategoryCapabilityPrefix + category);
                if (await _checker.IsAllowedAsync(viewer.PrincipalId, viewer.Kind, capability, Scope.Company, ct).ConfigureAwait(false))
                    allowed.Add(category);
            }

            return allowed.Count == 0 ? AuditViewScope.Denied : new AuditViewScope(false, allowed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return AuditViewScope.Denied;
        }
    }
}
