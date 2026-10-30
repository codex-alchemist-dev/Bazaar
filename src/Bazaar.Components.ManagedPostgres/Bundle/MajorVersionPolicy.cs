using System.Globalization;

namespace Bazaar.Components.ManagedPostgres.Bundle;

/// <summary>Outcome of comparing the bundle's major with an existing cluster.</summary>
/// <param name="Allowed">True when the host may start this cluster with this bundle.</param>
/// <param name="Message">Explanation and next step when not allowed; null when allowed.</param>
public sealed record MajorVersionDecision(bool Allowed, string? Message);

/// <summary>
/// Decides whether a bundle may start an existing cluster. Majors must match in either direction, because
/// the host never upgrades or downgrades a cluster on its own (packaging.md section 3, lifecycle step 4).
/// </summary>
public static class MajorVersionPolicy
{
    /// <summary>Checks the bundle major against the cluster's recorded major. A null cluster major means no cluster yet.</summary>
    public static MajorVersionDecision Evaluate(int bundleMajor, int? clusterMajor)
    {
        if (clusterMajor is null || clusterMajor == bundleMajor)
        {
            return new MajorVersionDecision(true, null);
        }

        var cluster = clusterMajor.Value.ToString(CultureInfo.InvariantCulture);
        var bundle = bundleMajor.ToString(CultureInfo.InvariantCulture);
        var message = clusterMajor < bundleMajor
            ? $"The cluster is PostgreSQL {cluster} but this bundle is PostgreSQL {bundle}. " +
              "Run 'bazaar db upgrade' to migrate the cluster with pg_upgrade before starting."
            : $"The cluster is PostgreSQL {cluster} but this bundle is PostgreSQL {bundle}. " +
              $"Downgrading is not supported. Start with the PostgreSQL {cluster} bundle, or restore a backup into a new cluster.";
        return new MajorVersionDecision(false, message);
    }
}
