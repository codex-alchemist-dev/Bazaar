namespace Bazaar.Server.TalentPool;

/// <summary>Compares two versions of one Talent template. Pure: no store, no clock.</summary>
public static class TalentVersionDiffer
{
    /// <summary>Above this many lines on either side the prompt is shown as a whole replacement, which bounds the cost.</summary>
    public const int MaxDiffLines = 2000;

    /// <summary>Diffs <paramref name="from"/> against <paramref name="to"/>.</summary>
    /// <exception cref="ArgumentException">The versions belong to different templates.</exception>
    public static ManifestDiff Diff(TalentTemplate from, TalentTemplate to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!string.Equals(from.TemplateId, to.TemplateId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Versions of different templates cannot be diffed.", nameof(to));
        }

        var a = from.Manifest;
        var b = to.Manifest;
        return new ManifestDiff(
            from.TemplateId,
            from.Version,
            to.Version,
            string.Equals(a.Name, b.Name, StringComparison.Ordinal) ? null : (a.Name, b.Name),
            DiffLines(a.Prompt, b.Prompt),
            Except(b.ToolAllowlist, a.ToolAllowlist),
            Except(a.ToolAllowlist, b.ToolAllowlist),
            Except(b.ModelWhitelist, a.ModelWhitelist),
            Except(a.ModelWhitelist, b.ModelWhitelist),
            Except(b.Files, a.Files),
            Except(a.Files, b.Files));
    }

    private static string[] Except(IReadOnlyList<string> source, IReadOnlyList<string> other) =>
        [.. source.Except(other, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');

    private static List<PromptLineChange> DiffLines(string oldText, string newText)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        var result = new List<PromptLineChange>();
        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines)
        {
            result.AddRange(a.Select(l => new PromptLineChange(LineChangeKind.Removed, l)));
            result.AddRange(b.Select(l => new PromptLineChange(LineChangeKind.Added, l)));
            return result;
        }

        // Longest common subsequence over lines, then walk it. Removed lines come before added ones at the same spot.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                result.Add(new PromptLineChange(LineChangeKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new PromptLineChange(LineChangeKind.Removed, a[x++]));
            }
            else
            {
                result.Add(new PromptLineChange(LineChangeKind.Added, b[y++]));
            }
        }

        while (x < a.Length)
        {
            result.Add(new PromptLineChange(LineChangeKind.Removed, a[x++]));
        }

        while (y < b.Length)
        {
            result.Add(new PromptLineChange(LineChangeKind.Added, b[y++]));
        }

        return result;
    }
}
