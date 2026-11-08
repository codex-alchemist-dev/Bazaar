using Bazaar.Server.Kanban.Ryusei;

namespace Bazaar.Server.Tests.Kanban.Ryusei;

/// <summary>The closed predicate set: validation and matching (doc 38, Trigger types). No I/O.</summary>
public sealed class RyuseiPredicateTests
{
    [Theory]
    [InlineData("task.transitioned")]
    [InlineData("task.due_passed")]
    [InlineData("task.blocked")]
    [InlineData("task.unblocked")]
    [InlineData("pr.merged")]
    [InlineData("pr.closed")]
    [InlineData("pr.review_requested")]
    [InlineData("gc.message")]
    [InlineData("vault.changed")]
    [InlineData("council.decision_recorded")]
    public void Validator_accepts_each_predicate_in_the_closed_set(string kind)
    {
        Assert.True(RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = kind }).IsValid);
    }

    [Theory]
    [InlineData("cron")]
    [InlineData("task.created")]
    [InlineData("gc.history")]
    [InlineData("Task.Transitioned")]
    [InlineData("task.transitioned ")]
    [InlineData("")]
    public void Validator_rejects_unknown_kinds_with_a_structured_error(string kind)
    {
        var result = RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = kind });

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Equal(RyuseiValidationCode.UnknownPredicateKind, result.Error.Code);
    }

    [Fact]
    public void Validator_refuses_a_gc_filter_on_a_predicate_that_does_not_take_one()
    {
        var result = RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = "task.transitioned", GcId = Guid.NewGuid() });

        Assert.Equal(RyuseiValidationCode.FilterNotAllowed, result.Error?.Code);
    }

    [Fact]
    public void Validator_refuses_an_author_filter_on_a_predicate_that_does_not_take_one()
    {
        var result = RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = "pr.merged", AuthorAgentId = Guid.NewGuid() });

        Assert.Equal(RyuseiValidationCode.FilterNotAllowed, result.Error?.Code);
    }

    [Fact]
    public void Validator_refuses_a_path_glob_on_a_predicate_other_than_vault_changed()
    {
        var result = RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = "gc.message", PathGlob = "docs/**" });

        Assert.Equal(RyuseiValidationCode.FilterNotAllowed, result.Error?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/docs/*.md")]
    [InlineData("docs/../secret.md")]
    [InlineData("docs\\*.md")]
    public void Validator_refuses_an_unsafe_path_glob(string glob)
    {
        var result = RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = "vault.changed", PathGlob = glob });

        Assert.Equal(RyuseiValidationCode.InvalidPathGlob, result.Error?.Code);
    }

    [Fact]
    public void Validator_accepts_a_relative_path_glob()
    {
        Assert.True(RyuseiPredicateValidator.Validate(new RyuseiPredicate { Kind = "vault.changed", PathGlob = "roadmap/**/*.md" }).IsValid);
    }

    [Theory]
    [InlineData("task.transitioned")]
    [InlineData("task.due_passed")]
    [InlineData("task.blocked")]
    [InlineData("task.unblocked")]
    [InlineData("pr.merged")]
    [InlineData("pr.closed")]
    [InlineData("pr.review_requested")]
    [InlineData("gc.message")]
    [InlineData("vault.changed")]
    [InlineData("council.decision_recorded")]
    public void Matches_each_kind_on_an_event_with_its_own_name(string kind)
    {
        var predicate = new RyuseiPredicate { Kind = kind };
        var matching = new RyuseiEvent { Kind = kind, VaultPath = "any/path.md" };

        Assert.True(RyuseiPredicateMatcher.Matches(predicate, matching));
    }

    [Fact]
    public void Matches_refuses_an_event_of_another_kind()
    {
        var predicate = new RyuseiPredicate { Kind = "task.transitioned" };

        Assert.False(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "task.blocked" }));
        Assert.False(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "Task.Transitioned" }));
    }

    [Fact]
    public void Matches_never_matches_an_unknown_predicate_kind()
    {
        Assert.False(RyuseiPredicateMatcher.Matches(new RyuseiPredicate { Kind = "cron" }, new RyuseiEvent { Kind = "cron" }));
    }

    [Fact]
    public void Gc_message_matches_any_author_when_no_author_filter_is_set()
    {
        var predicate = new RyuseiPredicate { Kind = "gc.message" };
        var @event = new RyuseiEvent { Kind = "gc.message", GcId = Guid.NewGuid(), AuthorAgentId = Guid.NewGuid() };

        Assert.True(RyuseiPredicateMatcher.Matches(predicate, @event));
    }

    [Fact]
    public void Gc_message_with_an_author_filter_matches_only_that_author()
    {
        var author = Guid.NewGuid();
        var predicate = new RyuseiPredicate { Kind = "gc.message", AuthorAgentId = author };

        Assert.True(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "gc.message", AuthorAgentId = author }));
        Assert.False(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "gc.message", AuthorAgentId = Guid.NewGuid() }));
        Assert.False(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "gc.message" }));
    }

    [Fact]
    public void Gc_message_with_a_gc_filter_matches_only_that_gc()
    {
        var gc = Guid.NewGuid();
        var predicate = new RyuseiPredicate { Kind = "gc.message", GcId = gc };

        Assert.True(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "gc.message", GcId = gc }));
        Assert.False(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "gc.message", GcId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("roadmap/**", "roadmap/objectives/a.md", true)]
    [InlineData("roadmap/*", "roadmap/a.md", true)]
    [InlineData("roadmap/*", "roadmap/objectives/a.md", false)]
    [InlineData("**/*.md", "a.md", true)]
    [InlineData("**/*.md", "x/y/a.md", true)]
    [InlineData("**/*.md", "x/y/a.txt", false)]
    [InlineData("docs/?.md", "docs/a.md", true)]
    [InlineData("docs/?.md", "docs/ab.md", false)]
    [InlineData("docs/a.md", "docs/a.md", true)]
    [InlineData("docs/a.md", "docs/a.mdx", false)]
    [InlineData("docs/a+b.md", "docs/a+b.md", true)]
    public void Vault_changed_matches_by_path_glob(string glob, string path, bool expected)
    {
        var predicate = new RyuseiPredicate { Kind = "vault.changed", PathGlob = glob };

        Assert.Equal(expected, RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "vault.changed", VaultPath = path }));
    }

    [Fact]
    public void Vault_changed_with_a_glob_does_not_match_an_event_without_a_path()
    {
        var predicate = new RyuseiPredicate { Kind = "vault.changed", PathGlob = "**" };

        Assert.False(RyuseiPredicateMatcher.Matches(predicate, new RyuseiEvent { Kind = "vault.changed" }));
    }
}
