namespace Bazaar.Client.Hive.Grants;

/// <summary>One cell of the matrix: whether the pair is granted for the kind. A project's cell to itself is never granted.</summary>
public sealed record HiveMatrixCell(HiveGrantKey Key, bool Granted, bool IsSelf);

/// <summary>One from-project row of one kind.</summary>
public sealed record HiveMatrixRow(string FromProject, IReadOnlyList<HiveMatrixCell> Cells);

/// <summary>The from-project by to-project grid for one kind.</summary>
public sealed record HiveMatrixKind(string Kind, IReadOnlyList<HiveMatrixRow> Rows);

/// <summary>The owner grant matrix: every project on either axis, and one grid per kind (doc 25 pane rules).</summary>
public sealed record HiveGrantMatrix(IReadOnlyList<string> Projects, IReadOnlyList<HiveMatrixKind> Kinds)
{
    /// <summary>
    /// Builds the matrix from the grants and the copy origin projects. Projects and kinds are sorted ordinally, so the
    /// layout does not depend on the order the server sent them in. A kind appears only when some grant names it.
    /// </summary>
    public static HiveGrantMatrix Build(IEnumerable<HiveGrant> grants, IEnumerable<string>? extraProjects = null)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var all = grants.ToArray();
        var projects = all.SelectMany(grant => new[] { grant.FromProject, grant.ToProject })
            .Concat(extraProjects ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToArray();
        var granted = new HashSet<HiveGrantKey>(all.Select(grant => grant.Key));
        var kinds = all.Select(grant => grant.Kind)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .Select(kind => new HiveMatrixKind(kind, projects
                .Select(from => new HiveMatrixRow(from, projects
                    .Select(to =>
                    {
                        var key = new HiveGrantKey(from, to, kind);
                        return new HiveMatrixCell(key, granted.Contains(key), string.Equals(from, to, StringComparison.Ordinal));
                    })
                    .ToArray()))
                .ToArray()))
            .ToArray();

        return new HiveGrantMatrix(projects, kinds);
    }
}
