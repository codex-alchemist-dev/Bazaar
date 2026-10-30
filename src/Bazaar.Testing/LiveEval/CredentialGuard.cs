using System.Text.RegularExpressions;

namespace Bazaar.Testing.LiveEval;

/// <summary>
/// One guard finding. It names the field path and the rule only; the matched value is never stored or printed.
/// </summary>
public sealed record CredentialFinding(string FieldPath, string Rule)
{
    public override string ToString() => $"{FieldPath}: {Rule}";
}

/// <summary>
/// Refuses a live eval when a key-shaped value would reach the task set or a file under the repo working tree.
/// Findings carry field paths and rule names only (doc 34, credential guard).
/// </summary>
public static partial class LiveEvalCredentialGuard
{
    public const string TaskSetRulePrefix = "task-set-key-prefix";
    public const string EnvironmentRule = "environment-key-under-repo-tree";

    /// <summary>
    /// Scans the task set and the process environment. <paramref name="environment"/> is the variable map the live eval
    /// would start with, <paramref name="repoRoot"/> is the repo working tree, and <paramref name="workingDirectory"/>
    /// is where the eval's child processes read and write.
    /// </summary>
    public static IReadOnlyList<CredentialFinding> Scan(
        TaskSet taskSet,
        IReadOnlyDictionary<string, string?> environment,
        string repoRoot,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(taskSet);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var findings = new List<CredentialFinding>();
        ScanTaskSet(taskSet, findings);
        ScanEnvironment(environment, repoRoot, workingDirectory, findings);
        return findings;
    }

    /// <summary>The rule name for a key prefix that matches <paramref name="text"/>, or null when none does.</summary>
    public static string? MatchKeyRule(string text)
    {
        foreach (var (regex, rule) in _keyRules)
        {
            if (regex.IsMatch(text))
            {
                return rule;
            }
        }

        return null;
    }

    private static void ScanTaskSet(TaskSet taskSet, List<CredentialFinding> findings)
    {
        CheckText("id", taskSet.Id, findings);
        for (var i = 0; i < taskSet.Cases.Count; i++)
        {
            var taskCase = taskSet.Cases[i];
            CheckText($"cases[{i}].id", taskCase.Id, findings);
            CheckText($"cases[{i}].prompt", taskCase.Prompt, findings);
        }
    }

    private static void CheckText(string fieldPath, string text, List<CredentialFinding> findings)
    {
        var rule = MatchKeyRule(text);
        if (rule is not null)
        {
            findings.Add(new CredentialFinding(fieldPath, $"{TaskSetRulePrefix}:{rule}"));
        }
    }

    private static void ScanEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string repoRoot,
        string workingDirectory,
        List<CredentialFinding> findings)
    {
        if (!IsUnder(workingDirectory, repoRoot))
        {
            return;
        }

        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrEmpty(value) && MatchKeyRule(value) is not null)
            {
                findings.Add(new CredentialFinding($"env:{name}", EnvironmentRule));
            }
        }
    }

    /// <summary>True when <paramref name="path"/> is the root or lies below it. Both paths are made absolute first.</summary>
    internal static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (fullPath.Equals(fullRoot, comparison))
        {
            return true;
        }

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    // A prefix only counts at a word boundary and with a body long enough to be a key, so prose such as "risk-averse"
    // does not match. Fixture keys in tests must still be shaped this way, and no real key may appear in the repo.
    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])ghp_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])xox[a-z]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackTokenPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])AKIA[0-9A-Z]{16}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyPattern();

    private static readonly (Regex Pattern, string Rule)[] _keyRules =
    [
        (ApiKeyPattern(), "api-key-prefix-sk"),
        (GitHubTokenPattern(), "github-token-prefix-ghp"),
        (SlackTokenPattern(), "slack-token-prefix-xox"),
        (AwsAccessKeyPattern(), "aws-access-key-prefix-akia"),
    ];
}
