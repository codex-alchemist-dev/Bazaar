using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>Checks shared by every grant store, so a grant between a project and itself or with an empty id never exists.</summary>
internal static class HiveGrantRules
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the grant names two different, non-empty projects.</summary>
    public static void Validate(HiveGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (string.IsNullOrWhiteSpace(grant.FromProject) || string.IsNullOrWhiteSpace(grant.ToProject)
            || string.Equals(grant.FromProject, grant.ToProject, StringComparison.Ordinal))
        {
            throw new ArgumentException("A grant needs two different projects.", nameof(grant));
        }
    }
}
