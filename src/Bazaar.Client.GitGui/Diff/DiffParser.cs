using System.Globalization;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Diff;

/// <summary>
/// Parses the unified-diff text in <see cref="GitPrFileDto.Patch"/> into a <see cref="DiffModel"/>. It accepts both a bare
/// hunk list (the forge file APIs) and a full <c>git diff</c> with header lines. It never throws on odd input: text it cannot
/// place becomes a context line, and a patch with no hunks becomes a notice.
/// </summary>
public static partial class DiffParser
{
    public static DiffModel Parse(GitPrFileDto file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var patch = file.Patch;
        if (string.IsNullOrEmpty(patch))
        {
            return Empty(file, null, DiffNotice.NoPatch);
        }

        var hunks = new List<DiffHunk>();
        string? oldPath = null;
        var binary = false;

        List<DiffLine>? current = null;
        string header = string.Empty;
        int oldStart = 0, newStart = 0, oldNo = 0, newNo = 0;

        void Close()
        {
            if (current is not null)
            {
                hunks.Add(new DiffHunk(header, oldStart, newStart, current));
                current = null;
            }
        }

        // A patch normally ends with a newline; the empty piece after it is not a line.
        var pieces = patch.Split('\n');
        var count = pieces[^1].Length == 0 ? pieces.Length - 1 : pieces.Length;
        for (var index = 0; index < count; index++)
        {
            var raw = pieces[index];
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var match = HunkHeader().Match(line);
            if (match.Success)
            {
                Close();
                oldStart = oldNo = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                newStart = newNo = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                header = line;
                current = [];
                continue;
            }

            if (current is null)
            {
                // Before the first hunk: git header lines.
                if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    oldPath = line["rename from ".Length..];
                }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    binary = true;
                }

                continue;
            }

            if (line.StartsWith('\\'))
            {
                current.Add(new DiffLine(DiffLineKind.NoNewline, null, null, line.TrimStart('\\', ' ')));
            }
            else if (line.StartsWith('+'))
            {
                current.Add(new DiffLine(DiffLineKind.Added, null, newNo++, line[1..]));
            }
            else if (line.StartsWith('-'))
            {
                current.Add(new DiffLine(DiffLineKind.Removed, oldNo++, null, line[1..]));
            }
            else
            {
                // A context line starts with one space; some tools strip the space from a blank line.
                current.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, line.StartsWith(' ') ? line[1..] : line));
            }
        }

        Close();

        if (hunks.Count == 0)
        {
            return Empty(file, oldPath, binary ? DiffNotice.Binary : DiffNotice.NoPatch);
        }

        var added = hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
        var removed = hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
        return new DiffModel(file.Path, oldPath, file.Status, file.Truncated, DiffNotice.None, hunks, added, removed);
    }

    private static DiffModel Empty(GitPrFileDto file, string? oldPath, DiffNotice notice) =>
        new(file.Path, oldPath, file.Status, file.Truncated, notice, [], file.Additions, file.Deletions);

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}
