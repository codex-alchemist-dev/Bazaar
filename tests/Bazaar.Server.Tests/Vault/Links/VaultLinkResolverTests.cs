using Bazaar.Server.Vault.Links;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Vault.Links;

public sealed class VaultLinkResolverTests
{
    private static readonly Func<VaultIndexEntry, bool> AllVisible = _ => true;

    private static readonly VaultIndexEntry Source = Entry("projects/a/Source.md", "Source", project: "a");

    [Fact]
    public void Path_match_strips_md_from_the_entry_path()
    {
        var target = Entry("projects/x/Plan.md", "Something else");
        var result = Resolve(Link("projects/x/Plan"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
        Assert.True(result.AnchorFound);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Path_match_strips_md_from_the_link_target()
    {
        var target = Entry("projects/x/Plan.md", "Something else");
        var result = Resolve(Link("projects/x/Plan.md"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
    }

    [Fact]
    public void Path_match_is_case_sensitive()
    {
        var target = Entry("projects/x/Plan.md", "Other");
        var result = Resolve(Link("projects/x/plan"), [target]);

        Assert.False(result.Resolved);
        Assert.Null(result.Target);
    }

    [Fact]
    public void Path_match_takes_precedence_over_title()
    {
        var byPath = Entry("Auth service.md", "Unrelated");
        var byTitle = Entry("notes/auth.md", "Auth service");
        var result = Resolve(Link("Auth service"), [byTitle, byPath]);

        Assert.True(result.Resolved);
        Assert.Same(byPath, result.Target);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Title_match_resolves_a_single_document()
    {
        var target = Entry("notes/auth.md", "Auth service");
        var result = Resolve(Link("Auth service"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Title_match_is_case_insensitive()
    {
        var target = Entry("notes/auth.md", "Auth service");
        var result = Resolve(Link("auth SERVICE"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
    }

    [Fact]
    public void Alias_match_resolves_a_single_document()
    {
        var target = Entry("notes/auth.md", "Auth service", aliases: ["Authentication", "AuthN"]);
        var result = Resolve(Link("authn"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
    }

    [Fact]
    public void Same_project_tie_break_resolves_an_ambiguous_title()
    {
        var sameProject = Entry("projects/a/Plan.md", "Plan", project: "a");
        var otherProject = Entry("projects/b/Plan.md", "Plan", project: "b");
        var result = Resolve(Link("Plan"), [otherProject, sameProject], VaultAmbiguousLinkPolicy.Refuse);

        Assert.True(result.Resolved);
        Assert.Same(sameProject, result.Target);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Same_project_tie_break_is_skipped_when_source_has_no_project()
    {
        var noProjectSource = Entry("Source.md", "Source", project: null);
        var a = Entry("projects/a/Plan.md", "Plan", project: "a");
        var b = Entry("projects/b/Plan.md", "Plan", project: "b");
        var result = Resolve(Link("Plan"), [b, a], VaultAmbiguousLinkPolicy.Refuse, source: noProjectSource);

        Assert.False(result.Resolved);
        Assert.Null(result.Target);
        Assert.Equal(["projects/a/Plan.md", "projects/b/Plan.md"], Paths(result.Candidates));
    }

    [Fact]
    public void Warn_policy_links_the_first_candidate_by_path_and_lists_all()
    {
        var first = Entry("b/Plan.md", "Plan");
        var second = Entry("a/Plan.md", "Plan");
        var result = Resolve(Link("Plan"), [first, second], VaultAmbiguousLinkPolicy.Warn);

        Assert.True(result.Resolved);
        Assert.Same(second, result.Target);
        Assert.Equal(["a/Plan.md", "b/Plan.md"], Paths(result.Candidates));
        Assert.False(string.IsNullOrEmpty(result.Warning));
        Assert.Contains("2", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_policy_leaves_a_true_ambiguity_unresolved_with_all_candidates()
    {
        var first = Entry("b/Plan.md", "Plan");
        var second = Entry("a/Plan.md", "Plan");
        var result = Resolve(Link("Plan"), [first, second], VaultAmbiguousLinkPolicy.Refuse);

        Assert.False(result.Resolved);
        Assert.Null(result.Target);
        Assert.False(result.AnchorFound);
        Assert.Equal(["a/Plan.md", "b/Plan.md"], Paths(result.Candidates));
        Assert.False(string.IsNullOrEmpty(result.Warning));
    }

    [Fact]
    public void Ambiguity_left_after_a_failed_same_project_tie_break_reports_the_original_matches()
    {
        var b = Entry("projects/b/Plan.md", "Plan", project: "b");
        var c = Entry("projects/c/Plan.md", "Plan", project: "c");
        var result = Resolve(Link("Plan"), [c, b], VaultAmbiguousLinkPolicy.Warn);

        Assert.True(result.Resolved);
        Assert.Same(b, result.Target);
        Assert.Equal(["projects/b/Plan.md", "projects/c/Plan.md"], Paths(result.Candidates));
    }

    [Fact]
    public void Hidden_duplicate_title_does_not_make_a_visible_title_ambiguous()
    {
        var visible = Entry("a/Plan.md", "Plan");
        var hidden = Entry("b/Plan.md", "Plan");
        var result = Resolve(Link("Plan"), [visible, hidden], VaultAmbiguousLinkPolicy.Refuse, canRead: e => e != hidden);

        Assert.True(result.Resolved);
        Assert.Same(visible, result.Target);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Hidden_entries_are_never_listed_as_candidates()
    {
        var visibleA = Entry("a/Plan.md", "Plan");
        var visibleB = Entry("c/Plan.md", "Plan");
        var hidden = Entry("b/Plan.md", "Plan");
        var result = Resolve(Link("Plan"), [visibleB, hidden, visibleA], VaultAmbiguousLinkPolicy.Refuse, canRead: e => e != hidden);

        Assert.False(result.Resolved);
        Assert.Equal(["a/Plan.md", "c/Plan.md"], Paths(result.Candidates));
        Assert.DoesNotContain(result.Candidates, e => e == hidden);
    }

    [Fact]
    public void Hidden_only_title_is_unresolved_with_no_warning()
    {
        var hidden = Entry("secret/Plan.md", "Secret plan");
        var result = Resolve(Link("Secret plan"), [hidden], VaultAmbiguousLinkPolicy.Warn, canRead: _ => false);

        Assert.False(result.Resolved);
        Assert.Null(result.Target);
        Assert.False(result.AnchorFound);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Hidden_only_path_is_unresolved_with_no_warning()
    {
        var hidden = Entry("secret/Plan.md", "Plan");
        var result = Resolve(Link("secret/Plan"), [hidden], VaultAmbiguousLinkPolicy.Warn, canRead: e => e != hidden);

        Assert.False(result.Resolved);
        Assert.Null(result.Target);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Same_document_link_without_anchor_targets_the_source()
    {
        var result = Resolve(Link(string.Empty), [], VaultAmbiguousLinkPolicy.Refuse);

        Assert.True(result.Resolved);
        Assert.Same(Source, result.Target);
        Assert.True(result.AnchorFound);
    }

    [Fact]
    public void Same_document_heading_link_is_found_in_the_source()
    {
        var source = Entry("a/Source.md", "Source", headings: ["token-flow"]);
        var link = Link(string.Empty, WikiLinkAnchorKind.Heading, "Token flow!");
        var result = Resolve(link, [], VaultAmbiguousLinkPolicy.Refuse, source: source);

        Assert.True(result.Resolved);
        Assert.Same(source, result.Target);
        Assert.True(result.AnchorFound);
    }

    [Fact]
    public void Same_document_heading_link_with_no_matching_heading_keeps_the_link_resolved()
    {
        var source = Entry("a/Source.md", "Source", headings: ["other"]);
        var link = Link(string.Empty, WikiLinkAnchorKind.Heading, "Token flow");
        var result = Resolve(link, [], VaultAmbiguousLinkPolicy.Refuse, source: source);

        Assert.True(result.Resolved);
        Assert.Same(source, result.Target);
        Assert.False(result.AnchorFound);
    }

    [Fact]
    public void Heading_anchor_is_found_when_its_slug_is_present()
    {
        var target = Entry("notes/auth.md", "Auth service", headings: ["overview", "token-flow"]);
        var result = Resolve(Link("Auth service", WikiLinkAnchorKind.Heading, "Token flow"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
        Assert.True(result.AnchorFound);
    }

    [Fact]
    public void Broken_heading_anchor_keeps_the_link_resolved()
    {
        var target = Entry("notes/auth.md", "Auth service", headings: ["overview"]);
        var result = Resolve(Link("Auth service", WikiLinkAnchorKind.Heading, "Token flow"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
        Assert.False(result.AnchorFound);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Heading_anchor_is_compared_by_slug_not_raw_text()
    {
        var target = Entry("notes/auth.md", "Auth service", headings: ["token-flow"]);
        var result = Resolve(Link("Auth service", WikiLinkAnchorKind.Heading, "TOKEN FLOW"), [target]);

        Assert.True(result.AnchorFound);
    }

    [Fact]
    public void Block_anchor_is_found_ignoring_case()
    {
        var target = Entry("notes/auth.md", "Auth service", blocks: ["a1b2c3"]);
        var result = Resolve(Link("Auth service", WikiLinkAnchorKind.Block, "A1B2C3"), [target]);

        Assert.True(result.Resolved);
        Assert.True(result.AnchorFound);
    }

    [Fact]
    public void Missing_block_anchor_keeps_the_link_resolved()
    {
        var target = Entry("notes/auth.md", "Auth service", blocks: ["a1b2c3"]);
        var result = Resolve(Link("Auth service", WikiLinkAnchorKind.Block, "ffffff"), [target]);

        Assert.True(result.Resolved);
        Assert.Same(target, result.Target);
        Assert.False(result.AnchorFound);
    }

    [Fact]
    public void Anchor_is_checked_on_the_warned_target_of_an_ambiguous_link()
    {
        var first = Entry("a/Plan.md", "Plan", headings: ["token-flow"]);
        var second = Entry("b/Plan.md", "Plan", headings: []);
        var result = Resolve(Link("Plan", WikiLinkAnchorKind.Heading, "Token flow"), [second, first], VaultAmbiguousLinkPolicy.Warn);

        Assert.True(result.Resolved);
        Assert.Same(first, result.Target);
        Assert.True(result.AnchorFound);
    }

    [Fact]
    public void Unresolved_link_has_no_anchor_found_even_without_an_anchor()
    {
        var result = Resolve(Link("Missing"), []);

        Assert.False(result.Resolved);
        Assert.Null(result.Target);
        Assert.False(result.AnchorFound);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Null_arguments_throw_argument_null_exception()
    {
        var link = Link("Plan");
        var index = new[] { Source };

        Assert.Throws<ArgumentNullException>(() => VaultLinkResolver.Resolve(null!, Source, index, AllVisible, VaultAmbiguousLinkPolicy.Warn));
        Assert.Throws<ArgumentNullException>(() => VaultLinkResolver.Resolve(link, null!, index, AllVisible, VaultAmbiguousLinkPolicy.Warn));
        Assert.Throws<ArgumentNullException>(() => VaultLinkResolver.Resolve(link, Source, null!, AllVisible, VaultAmbiguousLinkPolicy.Warn));
        Assert.Throws<ArgumentNullException>(() => VaultLinkResolver.Resolve(link, Source, index, null!, VaultAmbiguousLinkPolicy.Warn));
    }

    [Fact]
    public void Odd_entry_values_do_not_throw()
    {
        var odd = Entry(string.Empty, null, aliases: [""], project: "");
        var result = Resolve(Link(""), [odd]);

        Assert.True(result.Resolved);
        Assert.Same(Source, result.Target);
    }

    private static WikiLink Link(string target, WikiLinkAnchorKind anchorKind = WikiLinkAnchorKind.None, string? anchor = null) =>
        new(WikiLinkKind.Link, target, anchorKind, anchor, null, 0, 0);

    private static VaultIndexEntry Entry(
        string path,
        string? title,
        IReadOnlyList<string>? aliases = null,
        string? project = null,
        IReadOnlyList<string>? headings = null,
        IReadOnlyList<string>? blocks = null) =>
        new(Guid.NewGuid(), path, title, aliases ?? [], project, headings ?? [], blocks ?? []);

    private static LinkResolution Resolve(
        WikiLink link,
        IEnumerable<VaultIndexEntry> index,
        VaultAmbiguousLinkPolicy policy = VaultAmbiguousLinkPolicy.Warn,
        Func<VaultIndexEntry, bool>? canRead = null,
        VaultIndexEntry? source = null) =>
        VaultLinkResolver.Resolve(link, source ?? Source, index, canRead ?? AllVisible, policy);

    private static string[] Paths(IEnumerable<VaultIndexEntry> entries) =>
        entries.Select(e => e.Path).ToArray();
}
