namespace Bazaar.Server.Presence.Sources;

/// <summary>Sorts a tool into the class presence shows (doc 23: research, review, edit-or-build).</summary>
public interface IPresenceToolClassifier
{
    /// <summary>The class of the named tool; <see cref="Bazaar.Server.WorkLife.PresenceToolClass.None"/> when it is unknown.</summary>
    Bazaar.Server.WorkLife.PresenceToolClass Classify(string? toolName);
}

/// <summary>
/// The default classifier. A tool name is split into words at <c>. _ - / :</c>, and the first word that starts with a listed prefix
/// decides the class (research first, then review, then edit-or-build). The lists are options the operator can change; an unlisted
/// tool is unclassified and shows as plain "working".
/// </summary>
public sealed class NameTableToolClassifier(PresenceSourcesOptions options) : IPresenceToolClassifier
{
    private static readonly char[] _separators = ['.', '_', '-', '/', ':', ' '];

    /// <inheritdoc />
    public Bazaar.Server.WorkLife.PresenceToolClass Classify(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Bazaar.Server.WorkLife.PresenceToolClass.None;
        }

        var words = toolName.Split(_separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (prefixes, toolClass) in Tables())
        {
            if (words.Any(w => prefixes.Any(p => w.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                return toolClass;
            }
        }

        return Bazaar.Server.WorkLife.PresenceToolClass.None;
    }

    private (IReadOnlyList<string>, Bazaar.Server.WorkLife.PresenceToolClass)[] Tables() =>
    [
        (options.ResearchToolPrefixes, Bazaar.Server.WorkLife.PresenceToolClass.Research),
        (options.ReviewToolPrefixes, Bazaar.Server.WorkLife.PresenceToolClass.Review),
        (options.EditOrBuildToolPrefixes, Bazaar.Server.WorkLife.PresenceToolClass.EditOrBuild),
    ];
}

/// <summary>Options of the presence sources. Safe defaults ship; an operator may replace the lists.</summary>
public sealed class PresenceSourcesOptions
{
    /// <summary>Tool-name word prefixes of the research class.</summary>
    public IReadOnlyList<string> ResearchToolPrefixes { get; init; } = ["search", "read", "fetch", "browse", "grep", "glob", "list", "lookup", "query"];

    /// <summary>Tool-name word prefixes of the review class.</summary>
    public IReadOnlyList<string> ReviewToolPrefixes { get; init; } = ["review", "diff", "lint", "inspect", "audit"];

    /// <summary>Tool-name word prefixes of the edit-or-build class.</summary>
    public IReadOnlyList<string> EditOrBuildToolPrefixes { get; init; } = ["edit", "write", "create", "update", "delete", "build", "test", "run", "exec", "commit", "push", "patch"];
}
