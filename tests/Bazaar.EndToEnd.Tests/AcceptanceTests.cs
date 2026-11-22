using System.Text.RegularExpressions;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.EndToEnd.Tests.Harness;
using Bazaar.Server.Coding.Checks;

namespace Bazaar.EndToEnd.Tests;

/// <summary>
/// The real-task acceptance run (push plan 2026-11-30, critical path "agents do real work"; docs/design/17-coding-agent-harness.md
/// "Git and worktrees" and "Verification"). Same real server, gateway and wire client as <see cref="SmokeTests"/>, plus a real git
/// repository with one failing test. The owner asks the agent in the group chat to fix it; the model (scripted) answers with a
/// diff; the harness applies it in a worktree on <c>agent/&lt;task&gt;</c>, runs the repository's tests, runs the real doc 17 checks,
/// and commits. Steps no merged module performs yet are done by the harness and named in GAPS.md (gaps 9 to 11).
/// </summary>
public sealed partial class AcceptanceTests(EndToEndFixture env) : IClassFixture<EndToEndFixture>
{
    [GeneratedRegex("```diff\\n(?<diff>.*?)```", RegexOptions.Singleline)]
    private static partial Regex DiffBlock();

    /// <summary>Signs the owner in, asks the agent for the task over the wire, and returns the diff the agent's reply carries.</summary>
    private async Task<(string Reply, string Diff)> AskAgentAsync(string token, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var client = new WireClient(
            new WebSocketWireTransportFactory(env.Host.WireUri),
            new JsonWireCodec(),
            new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "e2e-device", DeviceClass = "headless", ClientBuild = "0.0.0-e2e", Credential = EndToEndFixture.DevToken },
            TimeProvider.System);
        var run = client.RunAsync(cts.Token);
        await client.SubscribeAsync(WireStreams.GcPrefix + EndToEndFixture.GcId.ToString("D"), cts.Token);

        var task = $"Task {token}: the test in tests/test.sh fails because src/add.sh subtracts. Fix it and reply with a diff.";
        await client.RequestAsync<GcSend, GcSendResult>(
            WireTypes.GcSend, new GcSend(EndToEndFixture.GcId, task, [EndToEndFixture.AgentId], null), cts.Token);

        string? reply = null;
        while (reply is null)
        {
            var message = await client.Messages.ReadAsync(cts.Token);
            if (message.AuthorId == EndToEndFixture.AgentId && message.Text.Contains(token, StringComparison.Ordinal)) reply = message.Text;
        }

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        var match = DiffBlock().Match(reply);
        Assert.True(match.Success, "The agent's reply has no diff block:\n" + reply);
        return (reply, match.Groups["diff"].Value);
    }

    private static async Task<IReadOnlyList<ChangedFile>> ChangedAgainstMainAsync(SampleRepo repo, string worktree, CancellationToken ct)
    {
        var names = await SampleRepo.GitAsync(worktree, ["diff", "--name-only", "main"], ct);
        var files = new List<ChangedFile>();
        foreach (var path in names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var oldText = await SampleRepo.GitAsync(worktree, ["show", "main:" + path], ct);
            var newPath = Path.Combine(worktree, path);
            files.Add(new ChangedFile(path, File.Exists(newPath) ? ChangeKind.Modified : ChangeKind.Deleted, oldText, File.Exists(newPath) ? await File.ReadAllTextAsync(newPath, ct) : null));
        }

        return files;
    }

    [Fact]
    public async Task The_agent_fixes_a_failing_test_in_its_own_worktree_and_the_work_is_committed()
    {
        env.RequireRunning();
        var ct = TestContext.Current.CancellationToken;
        using var repo = await SampleRepo.TryCreateAsync(ct);
        Assert.SkipWhen(repo is null, "git or sh is not available on this machine.");
        var token = "fix-" + Guid.NewGuid().ToString("N")[..8];
        // The model's decision is scripted; the server, gateway, group chat and wire are real.
        env.Model.AddScript(token, token + " I found it: add subtracts instead of adding.\n```diff\n" + SampleRepo.FixDiff + "```\nThen run tests/test.sh.");

        // Before: the repository's test fails on main.
        var before = await SampleRepo.RunTestsAsync(repo.MainPath, ct);
        Assert.Equal(1, before.ExitCode);

        var (_, diff) = await AskAgentAsync(token, ct);

        var worktree = await repo.AddWorktreeAsync(token, ct);
        await repo.ApplyAsync(worktree, diff, ct);
        var after = await SampleRepo.RunTestsAsync(worktree, ct);
        Assert.True(after.ExitCode == 0, after.Output);

        // The doc 17 checks run on the real diff: a source-only fix raises no flag.
        var changed = await ChangedAgainstMainAsync(repo, worktree, ct);
        Assert.Equal(["src/add.sh"], changed.Select(c => c.Path));
        Assert.Empty(TestFileChangeFlagger.Flag(changed));
        Assert.Empty(FakeSuccessScanner.Scan(changed));

        var commit = await SampleRepo.CommitAllAsync(worktree, "fix(add): add instead of subtract\n\nCo-Authored-By: Smoke <agent@bazaar.invalid>", ct);

        // The result: one commit on agent/<task>, main untouched and still failing, the worktree is its own directory.
        Assert.Equal(commit, (await SampleRepo.GitAsync(repo.MainPath, ["rev-parse", "agent/" + token], ct)).Trim());
        Assert.Equal("1", (await SampleRepo.GitAsync(repo.MainPath, ["rev-list", "--count", "main..agent/" + token], ct)).Trim());
        Assert.Equal(1, (await SampleRepo.RunTestsAsync(repo.MainPath, ct)).ExitCode);
        Assert.NotEqual(repo.MainPath, worktree);
    }

    [Fact]
    public async Task A_fix_that_weakens_the_test_is_flagged_and_not_committed()
    {
        env.RequireRunning();
        var ct = TestContext.Current.CancellationToken;
        using var repo = await SampleRepo.TryCreateAsync(ct);
        Assert.SkipWhen(repo is null, "git or sh is not available on this machine.");
        var token = "cheat-" + Guid.NewGuid().ToString("N")[..8];
        env.Model.AddScript(token, token + " Easy:\n```diff\n" + SampleRepo.CheatDiff + "```");

        var (_, diff) = await AskAgentAsync(token, ct);
        var worktree = await repo!.AddWorktreeAsync(token, ct);
        await repo.ApplyAsync(worktree, diff, ct);

        // The weakened test now passes, which is exactly why the pass alone is not trusted.
        Assert.Equal(0, (await SampleRepo.RunTestsAsync(worktree, ct)).ExitCode);
        var flags = TestFileChangeFlagger.Flag(await ChangedAgainstMainAsync(repo, worktree, ct));

        var flag = Assert.Single(flags);
        Assert.Equal(FlagCodes.TestFileChanged, flag.Code);
        Assert.Equal("tests/test.sh", flag.Path);
        // Nothing was committed: the branch is still at main.
        Assert.Equal("0", (await SampleRepo.GitAsync(repo.MainPath, ["rev-list", "--count", "main..agent/" + token], ct)).Trim());
    }
}
