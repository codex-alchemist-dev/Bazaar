namespace Bazaar.Server.RepoMap;

/// <summary>A request for a repo map.</summary>
/// <param name="Root">Worktree-relative directory to map. "." maps the whole worktree.</param>
/// <param name="TokenBudget">Most tokens the map may use, by the configured counter. Default 1000 (the Aider default). From 1 to 16000.</param>
/// <param name="FocusFiles">Worktree-relative files listed first, in the order given, before any ranking.</param>
public sealed record RepoMapRequest(string Root, int TokenBudget = 1000, IReadOnlyList<string>? FocusFiles = null);

/// <summary>A built repo map.</summary>
/// <param name="Text">The map. Each file is a line with its path; each symbol is an indented line with its line number and signature.</param>
/// <param name="IncludedFiles">Files with at least one symbol in <see cref="Text"/>.</param>
/// <param name="OmittedFiles">Candidate files left out because the budget ran out.</param>
/// <param name="IncludedSymbols">Symbol lines in <see cref="Text"/>.</param>
/// <param name="OmittedSymbols">Symbols left out, including those of omitted files.</param>
/// <param name="TokenEstimate">The counter's count of <see cref="Text"/>. Never above the requested budget.</param>
public sealed record RepoMapResult(
    string Text,
    int IncludedFiles,
    int OmittedFiles,
    int IncludedSymbols,
    int OmittedSymbols,
    int TokenEstimate)
{
    /// <summary>True when the budget left out any file or symbol.</summary>
    public bool Truncated => OmittedFiles > 0 || OmittedSymbols > 0;
}
