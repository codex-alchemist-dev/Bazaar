using Bazaar.Client.GitGui.Diff;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Tests;

public sealed class DiffParserTests
{
    private static GitPrFileDto File(string? patch, string status = "modified", bool truncated = false, string path = "a.txt") =>
        new(path, status, 0, 0, patch, truncated);

    [Fact]
    public void Parses_add_remove_context_with_line_numbers()
    {
        var model = DiffParser.Parse(File("@@ -1,3 +1,4 @@ ctx\n one\n-two\n+TWO\n+extra\n three\n"));

        var hunk = Assert.Single(model.Hunks);
        Assert.Equal((1, 1), (hunk.OldStart, hunk.NewStart));
        Assert.Equal(
            [
                (DiffLineKind.Context, 1, 1, "one"),
                (DiffLineKind.Removed, 2, (int?)null, "two"),
                (DiffLineKind.Added, null, 2, "TWO"),
                (DiffLineKind.Added, null, 3, "extra"),
                (DiffLineKind.Context, 3, 4, "three"),
            ],
            hunk.Lines.Select(l => (l.Kind, l.OldNumber, l.NewNumber, l.Text)));
        Assert.Equal((2, 1), (model.Additions, model.Deletions));
        Assert.Equal(DiffNotice.None, model.Notice);
    }

    [Fact]
    public void Parses_several_hunks()
    {
        var model = DiffParser.Parse(File("@@ -1 +1 @@\n-a\n+b\n@@ -10,2 +10,2 @@\n x\n-y\n+z\n"));

        Assert.Equal(2, model.Hunks.Count);
        Assert.Equal(10, model.Hunks[1].OldStart);
        Assert.Equal(DiffLineKind.HunkHeader, model.Rows()[0].Kind);
        Assert.Equal(model.Hunks.Sum(h => h.Lines.Count) + 2, model.Rows().Count);
    }

    [Fact]
    public void Rename_headers_give_the_old_path()
    {
        var patch = "diff --git a/old.txt b/new.txt\nsimilarity index 90%\nrename from old.txt\nrename to new.txt\n--- a/old.txt\n+++ b/new.txt\n@@ -1 +1 @@\n-a\n+b\n";
        var model = DiffParser.Parse(File(patch, "renamed", path: "new.txt"));

        Assert.Equal("old.txt", model.OldPath);
        Assert.Equal("new.txt", model.Path);
        Assert.Equal(2, model.Rows().Count - 1);
    }

    [Fact]
    public void Rename_without_a_patch_is_a_no_patch_notice()
    {
        var model = DiffParser.Parse(File(null, "renamed"));

        Assert.Equal(DiffNotice.NoPatch, model.Notice);
        Assert.Empty(model.Hunks);
    }

    [Fact]
    public void No_newline_marker_is_kept_and_has_no_numbers()
    {
        var model = DiffParser.Parse(File("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b\n\\ No newline at end of file\n"));

        var markers = model.Hunks[0].Lines.Where(l => l.Kind == DiffLineKind.NoNewline).ToList();
        Assert.Equal(2, markers.Count);
        Assert.All(markers, m => Assert.Null(m.OldNumber ?? m.NewNumber));
        Assert.Equal("No newline at end of file", markers[0].Text);
    }

    [Fact]
    public void Binary_patch_is_a_binary_notice()
    {
        var model = DiffParser.Parse(File("Binary files a/x.png and b/x.png differ\n"));
        Assert.Equal(DiffNotice.Binary, model.Notice);
    }

    [Fact]
    public void Missing_or_empty_patch_is_a_no_patch_notice()
    {
        Assert.Equal(DiffNotice.NoPatch, DiffParser.Parse(File(null)).Notice);
        Assert.Equal(DiffNotice.NoPatch, DiffParser.Parse(File(string.Empty)).Notice);
    }

    [Fact]
    public void Truncated_flag_carries_through()
    {
        var model = DiffParser.Parse(File("@@ -1 +1 @@\n-a\n+b\n", truncated: true));
        Assert.True(model.Truncated);
        Assert.Equal(2, model.Hunks[0].Lines.Count);
    }

    [Fact]
    public void Content_that_looks_like_a_header_inside_a_hunk_is_content()
    {
        var model = DiffParser.Parse(File("@@ -1,2 +1,2 @@\n--- not a header\n+++ also content\n"));

        Assert.Equal([DiffLineKind.Removed, DiffLineKind.Added], model.Hunks[0].Lines.Select(l => l.Kind));
        Assert.Equal("-- not a header", model.Hunks[0].Lines[0].Text);
    }

    [Fact]
    public void Windows_line_endings_are_stripped()
    {
        var model = DiffParser.Parse(File("@@ -1 +1 @@\r\n-a\r\n+b\r\n"));
        Assert.Equal(["a", "b"], model.Hunks[0].Lines.Select(l => l.Text));
    }

    [Fact]
    public void Context_window_collapses_far_lines()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 20).Select(i => " c" + i));
        var model = DiffParser.Parse(File("@@ -1,21 +1,22 @@\n" + lines + "\n+new\n")).WithContext(3);

        var kept = model.Hunks[0].Lines;
        Assert.Equal(DiffLineKind.Collapsed, kept[0].Kind);
        Assert.Equal("17 unchanged lines", kept[0].Text);
        Assert.Equal(1 + 3 + 1, kept.Count);
        Assert.Equal(DiffLineKind.Added, kept[^1].Kind);
    }

    [Fact]
    public void Context_window_leaves_a_hunk_with_no_change_alone()
    {
        var model = DiffParser.Parse(File("@@ -1,2 +1,2 @@\n a\n b\n")).WithContext(0);
        Assert.Equal(2, model.Hunks[0].Lines.Count);
    }
}
