using Bazaar.Client.Core.Launcher;

namespace Bazaar.Client.Core.Tests.Launcher;

public sealed class LaunchTests
{
    [Fact]
    public void Parse_collects_every_open_path_and_terminal_flag()
    {
        var request = LaunchArgs.Parse(["--open", "a.md", "--terminal", "--open", "b.cs"]);

        Assert.Equal(["a.md", "b.cs"], request.OpenPaths);
        Assert.True(request.OpenTerminal);
    }

    [Fact]
    public void Parse_ignores_unknown_arguments_and_dangling_open()
    {
        var request = LaunchArgs.Parse(["--verbose", "--open"]);

        Assert.Empty(request.OpenPaths);
        Assert.False(request.OpenTerminal);
    }

    [Fact]
    public async Task Forward_returns_false_when_no_instance_answers()
    {
        var name = $"bazaar-test-absent-{Guid.NewGuid():N}";

        var forwarded = await LaunchPipe.TryForwardAsync(
            name,
            LaunchRequest.Empty,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        Assert.False(forwarded);
    }

    [Fact]
    public async Task Forward_delivers_request_to_running_listener()
    {
        var name = $"bazaar-test-{Guid.NewGuid():N}";
        var sent = new LaunchRequest(["notes.md"], true);
        LaunchRequest? received = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var listener = LaunchPipe.ListenAsync(
            name,
            request =>
            {
                received = request;
                cts.Cancel();
                return Task.CompletedTask;
            },
            cts.Token);

        var forwarded = await LaunchPipe.TryForwardAsync(name, sent, TimeSpan.FromSeconds(5), CancellationToken.None);
        await listener;

        Assert.True(forwarded);
        Assert.NotNull(received);
        Assert.Equal(["notes.md"], received.OpenPaths);
        Assert.True(received.OpenTerminal);
    }

    [Fact]
    public void Default_pipe_name_is_per_user()
    {
        Assert.Equal("bazaar-launcher-alice", LaunchPipe.DefaultName("alice"));
        Assert.NotEqual(LaunchPipe.DefaultName("alice"), LaunchPipe.DefaultName("bob"));
    }
}
