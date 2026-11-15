using Bazaar.Client.Linux.Secrets;

namespace Bazaar.Client.Linux.Tests;

public class SecretServiceRefreshCredentialStoreTests
{
    /// <summary>Records every call and answers from a scripted queue.</summary>
    private sealed class FakeRunner : ISecretToolRunner
    {
        public List<(IReadOnlyList<string> Args, string? Stdin)> Calls { get; } = [];
        public Queue<SecretToolResult> Replies { get; } = new();

        public Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
        {
            Calls.Add((arguments.ToArray(), standardInput));
            return Task.FromResult(Replies.Dequeue());
        }
    }

    [Fact]
    public async Task Load_returns_null_when_no_keyring_entry_exists()
    {
        var runner = new FakeRunner();
        runner.Replies.Enqueue(new SecretToolResult(1, string.Empty));

        var store = new SecretServiceRefreshCredentialStore(runner, "srv-1");

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_strips_the_trailing_newline_from_the_secret()
    {
        var runner = new FakeRunner();
        runner.Replies.Enqueue(new SecretToolResult(0, "rt_secret_value\n"));

        var store = new SecretServiceRefreshCredentialStore(runner, "srv-1");

        Assert.Equal("rt_secret_value", await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_sends_the_secret_on_stdin_and_never_in_arguments()
    {
        var runner = new FakeRunner();
        runner.Replies.Enqueue(new SecretToolResult(0, string.Empty));

        await new SecretServiceRefreshCredentialStore(runner, "srv-1").SaveAsync("rt_secret_value", TestContext.Current.CancellationToken);

        var (args, stdin) = Assert.Single(runner.Calls);
        Assert.Equal("rt_secret_value", stdin);
        Assert.Equal("store", args[0]);
        Assert.DoesNotContain("rt_secret_value", args);
    }

    [Fact]
    public async Task Entries_are_scoped_by_application_purpose_and_server()
    {
        var runner = new FakeRunner();
        runner.Replies.Enqueue(new SecretToolResult(0, "x"));

        await new SecretServiceRefreshCredentialStore(runner, "srv-1").LoadAsync(TestContext.Current.CancellationToken);

        var args = runner.Calls[0].Args;
        Assert.Equal(["lookup", "application", "bazaar", "purpose", "refresh-credential", "server", "srv-1"], args);
    }

    [Fact]
    public async Task Clear_is_idempotent_when_the_entry_is_already_absent()
    {
        var runner = new FakeRunner();
        runner.Replies.Enqueue(new SecretToolResult(1, string.Empty));

        await new SecretServiceRefreshCredentialStore(runner, "srv-1").ClearAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_failing_keyring_throws_instead_of_reading_as_absent()
    {
        var runner = new FakeRunner();
        runner.Replies.Enqueue(new SecretToolResult(2, "partial"));

        var store = new SecretServiceRefreshCredentialStore(runner, "srv-1");

        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_blank_server_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SecretServiceRefreshCredentialStore(new FakeRunner(), " "));
    }
}
