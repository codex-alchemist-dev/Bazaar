namespace Bazaar.Client.GitGui;

/// <summary>
/// The Git client settings a pane reads (doc 52, Settings). A head backs it with its settings store; the shipped defaults are
/// in <see cref="GitGuiSettings"/>.
/// </summary>
public interface IGitGuiSettings
{
    /// <summary>PRs per list page (<c>git.pr_page_size</c>).</summary>
    int PrPageSize { get; }

    /// <summary>Unchanged lines kept around each change in a diff (<c>git.diff_context_lines</c>).</summary>
    int DiffContextLines { get; }
}

/// <summary>A setting key the module registers, with its shipped default and allowed range.</summary>
public sealed record GitGuiSettingDefinition(string Key, int DefaultValue, int Minimum, int Maximum, string Description);

/// <summary>The settings keys of this module and their shipped defaults (doc 52).</summary>
public sealed class GitGuiSettings : IGitGuiSettings
{
    public const string PrPageSizeKey = "git.pr_page_size";

    public const string DiffContextLinesKey = "git.diff_context_lines";

    public static IReadOnlyList<GitGuiSettingDefinition> Definitions { get; } =
    [
        new(PrPageSizeKey, 30, 1, 100, "Pull requests shown per page."),
        new(DiffContextLinesKey, 3, 0, 50, "Unchanged lines kept around each change in a diff."),
    ];

    /// <summary>The shipped defaults.</summary>
    public static GitGuiSettings Default { get; } = new();

    public int PrPageSize => 30;

    public int DiffContextLines => 3;
}
