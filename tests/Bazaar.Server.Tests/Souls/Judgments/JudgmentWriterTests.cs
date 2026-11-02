using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Judgments;

namespace Bazaar.Server.Tests.Souls.Judgments;

/// <summary>The judgment writer over fake ports (docs/design/07-soul-system.md, work package 4).</summary>
public sealed class JudgmentWriterTests
{
    [Fact]
    public async Task Recording_writes_one_decision_file_and_one_gc_message()
    {
        var files = new FakeDecisionFileWriter();
        var posts = new FakeGcJudgmentPoster();
        var writer = new JudgmentWriter(files, posts);

        var receipt = await writer.RecordAsync(JudgmentFixtures.Record(), TestContext.Current.CancellationToken);

        Assert.Single(files.Writes);
        Assert.Single(posts.Posts);
        Assert.Equal("decisions/0001-promote-ada-to-reviewer.md", receipt.DecisionFilePath);
        Assert.Equal(1, receipt.DecisionFileNumber);
        Assert.Equal(10, receipt.GcSeq);
        Assert.False(receipt.GcDuplicate);
        Assert.Equal(JudgmentFixtures.Record().DecisionId, receipt.DecisionId);
    }

    [Fact]
    public async Task The_message_names_the_soul_version_the_decision_file_and_the_decision_reference()
    {
        var files = new FakeDecisionFileWriter();
        var posts = new FakeGcJudgmentPoster();
        var writer = new JudgmentWriter(files, posts);

        await writer.RecordAsync(JudgmentFixtures.Record(), TestContext.Current.CancellationToken);

        var post = Assert.Single(posts.Posts);
        Assert.Equal(JudgmentFixtures.GcId, post.GcId);
        Assert.Equal(JudgmentFixtures.AgentId, post.AgentId);
        Assert.Equal(JudgmentFixtures.Record().DecisionId, post.DecisionId);
        Assert.Contains("soul version 7", post.Text);
        Assert.Contains($"decision {JudgmentFixtures.Record().DecisionId:D}", post.Text);
        Assert.Contains("Decision file: decisions/0001-promote-ada-to-reviewer.md", post.Text);
        Assert.Contains("Chose \"promote\"", post.Text);
    }

    [Fact]
    public async Task The_decision_file_is_written_before_the_message_is_posted()
    {
        var trace = new List<string>();
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(trace), new FakeGcJudgmentPoster(trace));

        await writer.RecordAsync(JudgmentFixtures.Record(), TestContext.Current.CancellationToken);

        Assert.Equal(["file", "post"], trace);
    }

    [Fact]
    public async Task A_failed_message_post_keeps_the_file_and_a_retry_completes_without_a_second_file()
    {
        var files = new FakeDecisionFileWriter();
        var posts = new FakeGcJudgmentPoster { FailWith = new InvalidOperationException("GC unavailable") };
        var writer = new JudgmentWriter(files, posts);
        var record = JudgmentFixtures.Record();

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.RecordAsync(record, TestContext.Current.CancellationToken));
        Assert.Single(files.Writes);
        Assert.Empty(posts.Posts);

        posts.FailWith = null;
        var retry = await writer.RecordAsync(record, TestContext.Current.CancellationToken);

        Assert.Single(files.Writes);
        Assert.Single(posts.Posts);
        Assert.Equal("decisions/0001-promote-ada-to-reviewer.md", retry.DecisionFilePath);
        Assert.False(retry.GcDuplicate);
    }

    [Fact]
    public async Task Repeating_a_recorded_judgment_returns_the_same_file_and_the_original_message()
    {
        var files = new FakeDecisionFileWriter();
        var posts = new FakeGcJudgmentPoster();
        var writer = new JudgmentWriter(files, posts);
        var record = JudgmentFixtures.Record();

        var first = await writer.RecordAsync(record, TestContext.Current.CancellationToken);
        var second = await writer.RecordAsync(record, TestContext.Current.CancellationToken);

        Assert.Equal(first.DecisionFilePath, second.DecisionFilePath);
        Assert.Equal(first.GcSeq, second.GcSeq);
        Assert.True(second.GcDuplicate);
    }

    [Fact]
    public async Task A_failed_file_write_posts_nothing()
    {
        var posts = new FakeGcJudgmentPoster();
        var files = new FakeDecisionFileWriter { FailWith = new IOException("Vault unavailable") };
        var writer = new JudgmentWriter(files, posts);

        await Assert.ThrowsAsync<IOException>(() => writer.RecordAsync(JudgmentFixtures.Record(), TestContext.Current.CancellationToken));

        Assert.Empty(posts.Posts);
    }

    [Fact]
    public async Task Reasoning_is_required()
    {
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(), new FakeGcJudgmentPoster());
        var record = JudgmentFixtures.Record() with { Reasoning = "   " };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => writer.RecordAsync(record, TestContext.Current.CancellationToken));
        Assert.Contains("Reasoning is required", ex.Message);
    }

    [Fact]
    public async Task A_judgment_must_run_under_a_soul_version_of_one_or_more()
    {
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(), new FakeGcJudgmentPoster());
        var record = JudgmentFixtures.Record() with { SoulVersion = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.RecordAsync(record, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_chosen_option_must_be_one_of_the_options_considered()
    {
        var files = new FakeDecisionFileWriter();
        var posts = new FakeGcJudgmentPoster();
        var writer = new JudgmentWriter(files, posts);
        var record = JudgmentFixtures.Record() with { Chosen = "retire" };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.RecordAsync(record, TestContext.Current.CancellationToken));

        Assert.Empty(files.Writes);
        Assert.Empty(posts.Posts);
    }

    [Fact]
    public async Task At_least_one_option_is_required()
    {
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(), new FakeGcJudgmentPoster());
        var record = JudgmentFixtures.Record() with { Options = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.RecordAsync(record, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Input_sequence_numbers_start_at_one()
    {
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(), new FakeGcJudgmentPoster());
        var record = JudgmentFixtures.Record() with { Inputs = [new JudgmentInput(0, SourceTrust.Owner)] };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.RecordAsync(record, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_identifiers_are_refused()
    {
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(), new FakeGcJudgmentPoster());
        var noDecision = JudgmentFixtures.Record() with { DecisionId = Guid.Empty };
        var noGc = JudgmentFixtures.Record() with { GcId = Guid.Empty };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.RecordAsync(noDecision, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.RecordAsync(noGc, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_null_record_is_refused()
    {
        var writer = new JudgmentWriter(new FakeDecisionFileWriter(), new FakeGcJudgmentPoster());

        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.RecordAsync(null!, TestContext.Current.CancellationToken));
    }
}
