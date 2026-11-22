using System.Text.RegularExpressions;

namespace Bazaar.Server.Coding.Checks;

/// <summary>
/// Fake-success scan (doc 17, Verification step 4). Looks for skips and disabled code added in a diff, and for removed tests or assertions.
/// Pre-existing lines are never flagged: added lines are compared as a multiset against the old text.
/// Only <c>.cs</c>, <c>.fs</c> and <c>.vb</c> files, and any file under a <c>tests/</c> folder, are inspected.
/// </summary>
public static partial class FakeSuccessScanner
{
    private static readonly string[] _skipMarkers = ["[Skip", "[Ignore", "Ignore("];
    private static readonly string[] _sourceExtensions = [".cs", ".fs", ".vb"];

    public static IReadOnlyList<FlagFinding> Scan(IReadOnlyList<ChangedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<FlagFinding>();
        foreach (var file in files.Where(Inspected))
        {
            var oldText = file.OldText ?? string.Empty;
            var newText = file.NewText ?? string.Empty;

            foreach (var line in AddedLines(oldText, newText))
            {
                if (_skipMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
                {
                    findings.Add(new FlagFinding(FlagCodes.SkipAdded, file.Path, line.Trim()));
                }

                if (line.Contains("#if false", StringComparison.Ordinal))
                {
                    findings.Add(new FlagFinding(FlagCodes.IfFalseAdded, file.Path, line.Trim()));
                }
            }

            var oldTests = TestAttribute().Count(oldText);
            var newTests = TestAttribute().Count(newText);
            if (newTests < oldTests)
            {
                findings.Add(new FlagFinding(FlagCodes.TestRemoved, file.Path, $"Test attributes {oldTests} -> {newTests}."));
            }

            var oldAsserts = AssertCall().Count(oldText);
            var newAsserts = AssertCall().Count(newText);
            if (newAsserts < oldAsserts)
            {
                findings.Add(new FlagFinding(FlagCodes.AssertionRemoved, file.Path, $"Assert. calls {oldAsserts} -> {newAsserts}."));
            }
        }

        return findings;
    }

    private static bool Inspected(ChangedFile file) =>
        _sourceExtensions.Any(extension => file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ||
        TestFileChangeFlagger.IsUnderTestsFolder(file.Path);

    /// <summary>Lines present in <paramref name="newText"/> beyond what <paramref name="oldText"/> already had, in order.</summary>
    private static List<string> AddedLines(string oldText, string newText)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(oldText))
        {
            remaining[line] = remaining.GetValueOrDefault(line) + 1;
        }

        var added = new List<string>();
        foreach (var line in Lines(newText))
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                added.Add(line);
            }
        }

        return added;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Length == 0 ? [] : text.Split('\n').Select(line => line.TrimEnd('\r'));

    [GeneratedRegex(@"\[(?:Fact|Test|AvaloniaFact)\b")]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"\bAssert\.")]
    private static partial Regex AssertCall();
}
