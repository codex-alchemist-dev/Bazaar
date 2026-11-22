namespace Bazaar.Server.Setup;

/// <summary>
/// Operator choices for fetching templates from git (doc 45 section 1.8, configurability rule). The defaults are the safe ones:
/// https and ssh only, a two-minute limit, and the system's own <c>git</c>.
/// </summary>
public sealed class GitTemplateOptions
{
    /// <summary>The URL schemes a template repository may use. <c>file</c> is for local mirrors and tests, and is off by default.</summary>
    public IReadOnlyList<string> AllowedSchemes { get; init; } = ["https", "ssh"];

    /// <summary>The git program to run. A bare name is looked up on PATH.</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary>How long one fetch may run before it is stopped.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The folder that holds fetched checkouts. A checkout is keyed by URL and commit, so a repeat resolve reuses it.</summary>
    public string CacheRoot { get; init; } = Path.Combine(Path.GetTempPath(), "bazaar-template-cache");
}
