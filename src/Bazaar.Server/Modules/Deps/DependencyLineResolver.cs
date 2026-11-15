namespace Bazaar.Server.Modules.Deps;

/// <summary>
/// Resolves a required version line against the lines an install has (docs/design/42-deps-as-modules.md section 9).
/// Version ranges are out of scope for this step: the match is by exact line id. Range matching (NuGet interval notation over
/// <c>version</c>) and the line-preference setting (<c>deps.postgresql.line</c>) come with the resolver work.
/// </summary>
public static class DependencyLineResolver
{
    /// <summary>
    /// Returns the installed line whose id equals <paramref name="requiredLine"/>, or a missing-line issue naming the
    /// module, the required line and the lines that are present.
    /// </summary>
    /// <param name="moduleId">The module the requirement is for, used in the message.</param>
    /// <param name="requiredLine">The line id the requirement names, for example <c>19</c>.</param>
    /// <param name="installedLines">The lines the install has for the module.</param>
    public static LineResolution Resolve(string moduleId, string requiredLine, IEnumerable<DependencyLine> installedLines)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(requiredLine);
        ArgumentNullException.ThrowIfNull(installedLines);

        var lines = installedLines.ToList();
        var match = lines.FirstOrDefault(l => string.Equals(l.Line, requiredLine, StringComparison.Ordinal));
        if (match is not null)
        {
            return new LineResolution(match, null);
        }

        var present = lines.Count == 0
            ? "none"
            : string.Join(", ", lines.Select(l => l.Line));

        return new LineResolution(
            null,
            new DependencyManifestIssue(
                DependencyManifestIssueCode.MissingLine,
                DependencyManifestIssueSeverity.Error,
                $"Module '{moduleId}' requires line '{requiredLine}', which is not installed. Installed lines: {present}."));
    }
}

/// <summary>The outcome of resolving a required line: the matched line, or the issue explaining why there is none.</summary>
/// <param name="Line">The matched line, or null when the line is missing.</param>
/// <param name="Issue">The missing-line error, or null when the line resolved.</param>
public sealed record LineResolution(DependencyLine? Line, DependencyManifestIssue? Issue)
{
    /// <summary>True when the required line resolved.</summary>
    public bool Succeeded => Line is not null;
}
