namespace Bazaar.Server.TalentPool.Tests;

public sealed class TalentVersionDifferTests
{
    private static TalentTemplate V(int version, string prompt, string[]? tools = null, string[]? models = null, string[]? files = null, string name = "Reviewer", string id = "reviewer") =>
        new(id, version, new SoulManifest(name, prompt, tools ?? [], models ?? ["anthropic:claude-haiku-5-5"], files ?? []), new string('a', 64), "a", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Identical_versions_are_empty()
    {
        var d = TalentVersionDiffer.Diff(V(1, "a\nb"), V(2, "a\nb"));
        Assert.True(d.IsEmpty);
        Assert.False(d.NeedsNewToolGrant);
    }

    [Fact]
    public void Prompt_diff_marks_added_removed_and_kept_lines_in_order()
    {
        var d = TalentVersionDiffer.Diff(V(1, "one\ntwo\nthree"), V(2, "one\n2\nthree\nfour"));

        Assert.Equal(
            [
                (LineChangeKind.Same, "one"),
                (LineChangeKind.Removed, "two"),
                (LineChangeKind.Added, "2"),
                (LineChangeKind.Same, "three"),
                (LineChangeKind.Added, "four"),
            ],
            d.PromptLines.Select(l => (l.Kind, l.Text)).ToArray());
    }

    [Fact]
    public void Line_endings_do_not_count_as_changes()
    {
        Assert.True(TalentVersionDiffer.Diff(V(1, "a\r\nb"), V(2, "a\nb")).IsEmpty);
    }

    [Fact]
    public void Tools_models_files_and_name_changes_are_reported_sorted()
    {
        var d = TalentVersionDiffer.Diff(
            V(1, "p", ["shell", "git"], ["a:one", "a:two"], ["x.md"]),
            V(2, "p", ["git", "browser", "web"], ["a:two", "b:three"], ["y.md"], "Reviewer Pro"));

        Assert.Equal(["browser", "web"], d.ToolsAdded);
        Assert.Equal(["shell"], d.ToolsRemoved);
        Assert.Equal(["b:three"], d.ModelsAdded);
        Assert.Equal(["a:one"], d.ModelsRemoved);
        Assert.Equal(["y.md"], d.FilesAdded);
        Assert.Equal(["x.md"], d.FilesRemoved);
        Assert.Equal(("Reviewer", "Reviewer Pro"), d.NameChange);
        Assert.True(d.NeedsNewToolGrant);
    }

    [Fact]
    public void Removing_a_tool_needs_no_new_grant()
    {
        Assert.False(TalentVersionDiffer.Diff(V(1, "p", ["shell"]), V(2, "p")).NeedsNewToolGrant);
    }

    [Fact]
    public void Empty_prompts_have_no_lines()
    {
        Assert.Empty(TalentVersionDiffer.Diff(V(1, ""), V(2, "")).PromptLines);
        var d = TalentVersionDiffer.Diff(V(1, ""), V(2, "x"));
        Assert.Equal([(LineChangeKind.Added, "x")], d.PromptLines.Select(l => (l.Kind, l.Text)).ToArray());
    }

    [Fact]
    public void A_huge_prompt_is_shown_as_a_whole_replacement()
    {
        var big = string.Join('\n', Enumerable.Range(0, TalentVersionDiffer.MaxDiffLines + 1));
        var d = TalentVersionDiffer.Diff(V(1, big), V(2, big + "\nmore"));
        Assert.DoesNotContain(d.PromptLines, l => l.Kind == LineChangeKind.Same);
    }

    [Fact]
    public void Different_templates_cannot_be_diffed()
    {
        Assert.Throws<ArgumentException>(() => TalentVersionDiffer.Diff(V(1, "p"), V(1, "p", id: "other")));
    }

    [Fact]
    public async Task The_in_memory_store_lists_versions_oldest_first()
    {
        var store = new InMemoryTalentTemplateStore();
        var ct = TestContext.Current.CancellationToken;
        await store.AddAsync(V(2, "b"), ct);
        await store.AddAsync(V(1, "a"), ct);

        int[] expected = [1, 2];
        Assert.Equal(expected, (await store.ListVersionsAsync("reviewer", ct)).Select(t => t.Version).ToArray());
        Assert.Empty(await store.ListVersionsAsync("nope", ct));
    }
}
