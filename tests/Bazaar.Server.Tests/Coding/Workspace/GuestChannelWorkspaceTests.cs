using Bazaar.Server.Coding.Workspace;

namespace Bazaar.Server.Tests.Coding.Workspace;

public class GuestChannelWorkspaceTests
{
    private const string _root = "/work/agent-1";
    private static readonly string[] _argv = ["git", "status"];
    private static readonly string[] _allTexts = ["one", "two", "three"];
    private static readonly string[] _tailTexts = ["two", "three"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProcessSpec Spec(TimeSpan? timeout = null) =>
        new(_argv, ".", new Dictionary<string, string>(), timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Scripted_exit_code_is_returned()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_argv, 3);
        await using var workspace = new GuestChannelWorkspace(channel, _root);

        var result = await workspace.RunAsync(Spec(), Ct);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Zero_exit_without_timeout_or_cancel_is_success()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_argv, 0);
        await using var workspace = new GuestChannelWorkspace(channel, _root);

        var result = await workspace.RunAsync(Spec(), Ct);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Output_is_numbered_and_replays_from_a_sequence()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_argv, 0, ("stdout", "one"), ("stdout", "two"), ("stderr", "three"));
        await using var workspace = new GuestChannelWorkspace(channel, _root);
        var handle = await workspace.StartAsync(Spec(), Ct);

        var all = await CollectAsync(workspace.StreamAsync(handle, 0, Ct));
        Assert.Equal(new long[] { 0, 1, 2 }, all.Select(c => c.Seq).ToArray());
        Assert.Equal(_allTexts, all.Select(c => c.Text).ToArray());

        var tail = await CollectAsync(workspace.StreamAsync(handle, 1, Ct));
        Assert.Equal(new long[] { 1, 2 }, tail.Select(c => c.Seq).ToArray());
        Assert.Equal(_tailTexts, tail.Select(c => c.Text).ToArray());
        Assert.Equal("stderr", tail[1].Stream);
    }

    [Fact]
    public async Task Stream_follows_a_running_process_until_it_ends()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(_argv, ("stdout", "first"));
        await using var workspace = new GuestChannelWorkspace(channel, _root);
        using var cts = new CancellationTokenSource();
        var handle = await workspace.StartAsync(Spec(), cts.Token);

        var seen = new List<OutputChunk>();
        await foreach (var chunk in workspace.StreamAsync(handle, 0, Ct))
        {
            seen.Add(chunk);
            cts.Cancel();
        }

        var only = Assert.Single(seen);
        Assert.Equal("first", only.Text);
    }

    [Fact]
    public async Task Guest_reported_timeout_is_not_success()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptTimeout(_argv, 0);
        await using var workspace = new GuestChannelWorkspace(channel, _root);

        var result = await workspace.RunAsync(Spec(), Ct);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Host_timeout_reports_TimedOut_and_never_success()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(_argv);
        await using var workspace = new GuestChannelWorkspace(channel, _root);

        var result = await workspace.RunAsync(Spec(TimeSpan.FromMilliseconds(50)), Ct);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Cancelling_the_start_token_cancels_the_process()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(_argv);
        await using var workspace = new GuestChannelWorkspace(channel, _root);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await workspace.RunAsync(Spec(), cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Dispose_cancels_running_processes()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(_argv);
        var workspace = new GuestChannelWorkspace(channel, _root);
        await workspace.StartAsync(Spec(), Ct);

        await workspace.DisposeAsync();

        Assert.Equal(0, channel.ActiveExecutions);
        Assert.Single(channel.Requests);
    }

    [Fact]
    public async Task Exec_request_carries_the_resolved_cwd_env_and_timeout()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_argv, 0);
        await using var workspace = new GuestChannelWorkspace(channel, _root);
        var env = new Dictionary<string, string> { ["LANG"] = "C" };

        await workspace.RunAsync(new ProcessSpec(_argv, "src/app", env, TimeSpan.FromMinutes(1)), Ct);

        var request = Assert.Single(channel.Requests);
        Assert.Equal("/work/agent-1/src/app", request.Cwd);
        Assert.Equal("C", request.Env["LANG"]);
        Assert.Equal(TimeSpan.FromMinutes(1), request.Timeout);
    }

    [Fact]
    public async Task A_working_directory_outside_the_root_is_refused()
    {
        var channel = new InMemoryGuestChannel();
        await using var workspace = new GuestChannelWorkspace(channel, _root);

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.StartAsync(new ProcessSpec(_argv, "../other", new Dictionary<string, string>(), TimeSpan.FromSeconds(5)), Ct));
        Assert.Empty(channel.Requests);
    }

    private static async Task<List<OutputChunk>> CollectAsync(IAsyncEnumerable<OutputChunk> stream)
    {
        var chunks = new List<OutputChunk>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }
}
