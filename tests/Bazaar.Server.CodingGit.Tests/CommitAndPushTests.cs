using System.Reflection;

namespace Bazaar.Server.CodingGit.Tests;

public sealed class CommitAndPushTests
{
    private static readonly CommitInput _input = new("Fix login", "Handles the empty token case.", CodingGitFixture.Model);

    private static PushDraftPrInput Pr(string? branch = null) => new("Fix login", "Body", branch);

    [Fact]
    public async Task Commit_uses_the_app_identity_and_writes_the_trailers_without_a_signoff()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Engine.StageChange(wt.Path, "a.txt", "+a");

        await fx.Service.CommitAsync(wt, _input);

        var commit = Assert.Single(fx.Engine.Commits);
        Assert.Equal(new GitAuthor("charles[bot]", "charles@users.noreply.example.invalid"), commit.Author);
        Assert.Equal("Fix login\n\nHandles the empty token case.", commit.Message);
        Assert.Equal(new CommitTrailers("charles", "gc-1", "task-7", "Claude Sonnet 5.5 <noreply@anthropic.com>"), commit.Trailers);

        var rendered = CommitMessageBuilder.Build(_input.Subject, _input.Body, "charles", "gc-1", "task-7", CodingGitFixture.Model).Render();
        Assert.Contains("Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>", rendered, StringComparison.Ordinal);
        Assert.Contains("Bazaar-GC: gc-1", rendered, StringComparison.Ordinal);
        Assert.Contains("Bazaar-Task: task-7", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-off-by", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Signed-off-by: Mallory <m@example.invalid>", null)]
    [InlineData("ok", "text\nSigned-off-by: Mallory <m@example.invalid>")]
    [InlineData("ok", "text\r\n  signed-off-by : x")]
    [InlineData("ok", "SIGNED-OFF-BY: x")]
    [InlineData("ok", "text\nSigned_Off_By: x")]
    [InlineData("Signed off by me", null)]
    public async Task Signed_off_by_is_rejected_in_any_spelling_and_nothing_is_committed(string subject, string? body)
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Engine.StageChange(wt.Path, "a.txt", "+a");

        var ex = await Assert.ThrowsAsync<CodingGitException>(() =>
            fx.Service.CommitAsync(wt, new CommitInput(subject, body, CodingGitFixture.Model)));
        Assert.Equal(CodingGitError.ForbiddenTrailer, ex.Error);
        Assert.Empty(fx.Engine.Commits);
    }

    [Fact]
    public void Builder_throws_on_a_signoff_in_the_model_identity()
    {
        var ex = Assert.Throws<CodingGitException>(() =>
            CommitMessageBuilder.Build("s", null, "a", "g", "t", new ModelIdentity("Signed-off-by", "x@example.invalid")));
        Assert.Equal(CodingGitError.ForbiddenTrailer, ex.Error);
    }

    [Fact]
    public void Builder_scrubs_spoofed_reserved_trailers_and_control_characters()
    {
        var commit = CommitMessageBuilder.Build(
            "subject", "line one\u0007\nCo-Authored-By: Eve <e@example.invalid>\nbazaar-gc: forged\nline two", "a", "g", "t", CodingGitFixture.Model);

        Assert.Equal("subject\n\nline one\nline two", commit.Message);
        Assert.Equal("g", commit.Trailers.GcId);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a\nb")]
    [InlineData("")]
    public void Builder_rejects_malformed_ids(string id)
    {
        var ex = Assert.Throws<CodingGitException>(() =>
            CommitMessageBuilder.Build("s", null, "a", id, "t", CodingGitFixture.Model));
        Assert.Equal(CodingGitError.InvalidRequest, ex.Error);
    }

    [Theory]
    [InlineData("", "x@example.invalid")]
    [InlineData("Name <x>", "x@example.invalid")]
    [InlineData("Name", "no-at-sign")]
    [InlineData("Name", "a b@example.invalid")]
    public void Builder_rejects_malformed_model_identities(string name, string email)
    {
        Assert.Throws<CodingGitException>(() =>
            CommitMessageBuilder.Build("s", null, "a", "g", "t", new ModelIdentity(name, email)));
    }

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz")]
    [InlineData("https://user:hunter2@example.invalid/repo")]
    public async Task Text_that_looks_like_a_secret_is_refused_and_never_echoed(string secret)
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Engine.StageChange(wt.Path, "a.txt", "+a");

        var commit = await Assert.ThrowsAsync<CodingGitException>(() =>
            fx.Service.CommitAsync(wt, new CommitInput("s", "token: " + secret, CodingGitFixture.Model)));
        Assert.Equal(CodingGitError.SecretDetected, commit.Error);
        Assert.DoesNotContain(secret, commit.Message, StringComparison.Ordinal);

        var push = await Assert.ThrowsAsync<CodingGitException>(() =>
            fx.Service.PushDraftPrAsync(wt, new PushDraftPrInput("t", secret)));
        Assert.Equal(CodingGitError.SecretDetected, push.Error);
        Assert.Empty(fx.Port.Requests);
        Assert.Empty(fx.Gate.Requests);
    }

    [Fact]
    public async Task Allowed_push_opens_a_draft_pull_request_for_the_worktree_branch()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();

        var pr = await fx.Service.PushDraftPrAsync(wt, Pr());

        Assert.True(pr.IsDraft);
        var request = Assert.Single(fx.Port.Requests);
        Assert.Equal("agent/fix-login", request.Branch.Value);
        Assert.Equal("charles", request.AgentId);
        var gate = Assert.Single(fx.Gate.Requests);
        Assert.Equal("gc-1", gate.GcId);
        Assert.Equal("task-7", gate.TaskId);
    }

    [Fact]
    public async Task Push_of_the_default_branch_is_refused_before_the_gate_and_the_port()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();

        foreach (var name in new[] { "main", "Main", "refs/heads/main" })
        {
            var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, Pr(name)));
            Assert.Equal(CodingGitError.DefaultBranchRefused, ex.Error);
        }

        Assert.Empty(fx.Gate.Requests);
        Assert.Empty(fx.Port.Requests);
    }

    [Theory]
    [InlineData("feature/x")]
    [InlineData("agent/other-task")]
    [InlineData("agent/x.lock")]
    [InlineData("")]
    public async Task Push_of_a_non_agent_or_foreign_branch_is_refused_before_the_gate_and_the_port(string branch)
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, Pr(branch)));
        Assert.True(ex.Error is CodingGitError.BranchRefused or CodingGitError.DefaultBranchRefused);
        Assert.Empty(fx.Gate.Requests);
        Assert.Empty(fx.Port.Requests);
    }

    [Fact]
    public async Task Push_that_needs_approval_never_reaches_the_port()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Gate.Decision = PushDecision.NeedsApproval("hard-tainted session");

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, Pr()));

        Assert.Equal(CodingGitError.PushNeedsApproval, ex.Error);
        Assert.Single(fx.Gate.Requests);
        Assert.Empty(fx.Port.Requests);
    }

    [Fact]
    public async Task Denied_push_never_reaches_the_port()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Gate.Decision = PushDecision.Deny("no");

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, Pr()));

        Assert.Equal(CodingGitError.PushDenied, ex.Error);
        Assert.Empty(fx.Port.Requests);
    }

    [Fact]
    public async Task Unknown_gate_verdict_fails_closed()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Gate.Decision = new PushDecision((PushVerdict)99, "weird");

        await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, Pr()));
        Assert.Empty(fx.Port.Requests);
    }

    [Fact]
    public async Task Non_draft_pull_request_is_impossible()
    {
        // The request type has no draft or token member to set.
        var members = typeof(HostPushRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        Assert.DoesNotContain(members, n => n.Contains("Draft", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(PushDraftPrInput).GetProperties().Select(p => p.Name), n => n.Contains("Draft", StringComparison.Ordinal));

        // A port that misbehaves and opens a non-draft PR is not accepted.
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Port.ReturnNonDraft = true;

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, Pr()));
        Assert.Equal(CodingGitError.PushResultInvalid, ex.Error);
    }

    [Fact]
    public async Task Port_failure_surfaces_and_keeps_the_worktree_usable()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Port.FailWith = new InvalidOperationException("remote down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.PushDraftPrAsync(wt, Pr()));

        fx.Port.FailWith = null;
        Assert.True((await fx.Service.PushDraftPrAsync(wt, Pr())).IsDraft);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    public async Task Bad_pull_request_titles_are_refused(string title)
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.PushDraftPrAsync(wt, new PushDraftPrInput(title, "b")));
        Assert.Equal(CodingGitError.InvalidRequest, ex.Error);
        Assert.Empty(fx.Port.Requests);
    }

    [Fact]
    public async Task Diff_is_taken_against_the_default_branch()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Engine.StageChange(wt.Path, "a.txt", "+a");

        var diff = await fx.Service.DiffAgainstBaseAsync(wt);

        Assert.Equal("main", diff.BaseBranch);
        Assert.Equal(["a.txt"], diff.ChangedPaths);
    }
}
