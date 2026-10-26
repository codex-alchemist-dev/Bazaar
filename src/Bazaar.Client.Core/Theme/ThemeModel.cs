namespace Bazaar.Client.Core.Theme;

/// <summary>Base polarity of a theme. Monaco maps it to vs or vs-dark.</summary>
public enum ThemeBase
{
    Light,
    Dark,
}

/// <summary>
/// A validated theme. Token values are canonical strings: colours as #RRGGBB or #RRGGBBAA in upper case,
/// lengths as decimal integers (pixels), flags as true or false, font families as written.
/// </summary>
public sealed record ThemeDefinition(
    int SchemaVersion,
    string Name,
    ThemeBase Base,
    IReadOnlyDictionary<string, string> Tokens);

/// <summary>One validation problem. Path is a JSON pointer into the theme file; Code is stable for tests.</summary>
public sealed record ThemeIssue(string Path, string Code, string Message);

/// <summary>Result of loading a theme file. Definition is null whenever any issue was found.</summary>
public sealed record ThemeLoadResult(ThemeDefinition? Definition, IReadOnlyList<ThemeIssue> Issues)
{
    public bool IsValid => Definition is not null;
}
