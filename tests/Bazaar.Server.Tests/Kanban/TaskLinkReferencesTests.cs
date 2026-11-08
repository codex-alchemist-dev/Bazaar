using Bazaar.Server.Kanban;

namespace Bazaar.Server.Tests.Kanban;

/// <summary>Pure shape checks for task link references (doc 12, Links). No database and no GC lookup.</summary>
public class TaskLinkReferencesTests
{
    [Theory]
    [InlineData("codex-alchemist-dev/Bazaar#1")]
    [InlineData("a/b#2")]
    [InlineData("Owner-Name/repo.name_v2#12345")]
    [InlineData("o/r#9007199254740993")]
    [InlineData("x/y.z#7")]
    public void A_well_formed_pr_reference_is_accepted(string reference) =>
        Assert.Null(TaskLinkReferences.Check(TaskLinkKind.Pr, reference));

    [Theory]
    [InlineData("")]
    [InlineData("Bazaar#1")]
    [InlineData("owner/repo")]
    [InlineData("owner/repo#")]
    [InlineData("owner/repo#0")]
    [InlineData("owner/repo#01")]
    [InlineData("owner/repo#-1")]
    [InlineData("owner/repo#1.5")]
    [InlineData("owner/repo#abc")]
    [InlineData("owner/repo#99999999999999999999999")]
    [InlineData("owner/repo#1 ")]
    [InlineData("owner/repo#1\n")]
    [InlineData(" owner/repo#1")]
    [InlineData("owner/repo#1/extra")]
    [InlineData("owner/repo/extra#1")]
    [InlineData("owner//repo#1")]
    [InlineData("-owner/repo#1")]
    [InlineData("owner-/repo#1")]
    [InlineData("own--er/repo#1")]
    [InlineData("owner_x/repo#1")]
    [InlineData("owner/.#1")]
    [InlineData("owner/..#1")]
    [InlineData("owner/re po#1")]
    [InlineData("owner/repo!#1")]
    [InlineData("https://github.com/owner/repo/pull/1")]
    public void A_malformed_pr_reference_is_refused(string reference) =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Pr, reference));

    [Fact]
    public void A_null_pr_reference_is_refused() =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Pr, null));

    [Fact]
    public void An_owner_name_longer_than_39_characters_is_refused()
    {
        var owner = new string('a', 40);
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Pr, $"{owner}/repo#1"));
        Assert.Null(TaskLinkReferences.Check(TaskLinkKind.Pr, $"{new string('a', 39)}/repo#1"));
    }

    [Fact]
    public void A_repository_name_longer_than_100_characters_is_refused()
    {
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Pr, $"owner/{new string('r', 101)}#1"));
        Assert.Null(TaskLinkReferences.Check(TaskLinkKind.Pr, $"owner/{new string('r', 100)}#1"));
    }

    [Theory]
    [InlineData("abcdef1")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("deadbeef")]
    public void A_lowercase_hex_commit_of_7_to_40_characters_is_accepted(string reference) =>
        Assert.Null(TaskLinkReferences.Check(TaskLinkKind.Commit, reference));

    [Theory]
    [InlineData("")]
    [InlineData("abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("ABCDEF1")]
    [InlineData("abcdefg")]
    [InlineData("abcdef1 ")]
    [InlineData("abcdef1\n")]
    [InlineData("abc-def1")]
    public void A_malformed_commit_is_refused(string reference) =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Commit, reference));

    [Fact]
    public void A_null_commit_is_refused() =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Commit, null));

    [Fact]
    public void A_canonical_gc_guid_is_accepted() =>
        Assert.Null(TaskLinkReferences.Check(TaskLinkKind.Gc, Guid.CreateVersion7().ToString("D")));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    [InlineData("3f2504e04f8911d39a0c0305e82c3301")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c330")]
    public void A_gc_reference_that_is_not_a_canonical_guid_is_refused(string reference) =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Gc, reference));

    [Fact]
    public void A_null_gc_reference_is_refused() =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.Gc, null));

    [Theory]
    [InlineData("notes/plan.md")]
    [InlineData("plan.md")]
    [InlineData("docs/design/12-kanban.md")]
    [InlineData("a/.hidden.md")]
    [InlineData("name with spaces.md")]
    public void A_vault_relative_markdown_path_is_accepted(string reference) =>
        Assert.Null(TaskLinkReferences.Check(TaskLinkKind.VaultDoc, reference));

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd.md")]
    [InlineData("/notes/plan.md")]
    [InlineData("notes/../secret.md")]
    [InlineData("../secret.md")]
    [InlineData("..md/..")]
    [InlineData("notes/./plan.md")]
    [InlineData("notes//plan.md")]
    [InlineData("notes/plan.md/")]
    [InlineData("notes\\plan.md")]
    [InlineData("notes/plan.txt")]
    [InlineData("notes/plan.MD")]
    [InlineData("notes/plan.md.bak")]
    [InlineData("notes/.md")]
    [InlineData("notes/plan\n.md")]
    [InlineData("notes/plan\t.md")]
    [InlineData("notes/pl\0an.md")]
    public void A_malformed_vault_path_is_refused(string reference) =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.VaultDoc, reference));

    [Fact]
    public void A_null_vault_path_is_refused() =>
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.VaultDoc, null));

    [Fact]
    public void A_vault_path_over_1024_characters_is_refused()
    {
        var longPath = new string('a', 1010) + "/" + new string('b', 20) + ".md";
        Assert.True(longPath.Length > 1024);
        Assert.NotNull(TaskLinkReferences.Check(TaskLinkKind.VaultDoc, longPath));
    }
}
