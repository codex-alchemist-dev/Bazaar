using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Chat;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Linux.Tests;

public sealed class LinuxChatSettingsTests : IDisposable
{
    private static readonly Guid _gcA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _gcB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-linux-chat-" + Guid.NewGuid().ToString("N"));
    private readonly ServerEndpoint _endpoint = new(new Uri("http://127.0.0.1:9000/"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LinuxPaths Paths => new(_dir);

    private static async Task<InMemorySessionStore> SignedIn()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(new ClientSession("stored-token", "refresh", DateTimeOffset.UtcNow.AddHours(1)), Ct);
        return store;
    }

    private ChatConnectionSettings? Resolve(string[] args, Dictionary<string, string> env, ISessionStore store, ServerEndpoint? endpoint = null, bool hasEndpoint = true) =>
        LinuxChatSettings.Resolve(
            args,
            name => env.GetValueOrDefault(name),
            Paths,
            store,
            () => hasEndpoint ? endpoint ?? _endpoint : null,
            TimeProvider.System,
            _ => Task.FromResult(false));

    [Fact]
    public async Task Environment_wins_over_the_stored_choice()
    {
        var store = await SignedIn();
        LastGcStore.Save(Paths.LastGcFile, new LastGcStore.Entry(_gcA, "ws://127.0.0.1:9000/ws"));
        var env = new Dictionary<string, string>
        {
            [ChatConnectionSettings.ServerVariable] = "ws://dev.example/ws",
            [ChatConnectionSettings.GcVariable] = _gcB.ToString(),
            [ChatConnectionSettings.TokenVariable] = "env-token",
        };

        var settings = Resolve([], env, store);

        Assert.Equal(_gcB, settings?.GcId);
        Assert.Equal("env-token", settings?.Token);
        Assert.Equal("dev.example", settings?.Server.Host);
    }

    [Fact]
    public async Task Stored_server_and_gc_are_used_when_the_environment_is_empty()
    {
        var store = await SignedIn();
        LastGcStore.Save(Paths.LastGcFile, new LastGcStore.Entry(_gcA, "ws://127.0.0.1:9000/ws"));

        var settings = Resolve([], [], store);

        Assert.Equal(_gcA, settings?.GcId);
        Assert.Equal("stored-token", settings?.Token);
        Assert.Equal(new Uri("ws://127.0.0.1:9000/ws"), settings?.Server);
    }

    [Fact]
    public async Task A_gc_remembered_for_another_server_is_not_used()
    {
        var store = await SignedIn();
        LastGcStore.Save(Paths.LastGcFile, new LastGcStore.Entry(_gcA, "wss://other.example/ws"));

        Assert.Null(Resolve([], [], store));
    }

    [Fact]
    public void No_session_means_no_connection()
    {
        LastGcStore.Save(Paths.LastGcFile, new LastGcStore.Entry(_gcA, "ws://127.0.0.1:9000/ws"));

        Assert.Null(Resolve([], [], new InMemorySessionStore()));
    }

    [Fact]
    public async Task A_missing_gc_list_falls_back_to_the_last_used_gc_and_dev_gc_sets_it()
    {
        var store = await SignedIn();

        Assert.Null(Resolve([], [], store));

        var first = Resolve([LinuxChatSettings.DevGcFlag, _gcB.ToString()], [], store);
        Assert.Equal(_gcB, first?.GcId);

        // The choice is remembered, so the next plain start opens it.
        Assert.Equal(_gcB, Resolve([], [], store)?.GcId);
    }

    [Fact]
    public async Task A_corrupt_last_gc_file_is_ignored()
    {
        var store = await SignedIn();
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Paths.LastGcFile, "{ not json", Ct);

        Assert.Null(Resolve([], [], store));
    }

    [Fact]
    public async Task A_refused_or_missing_endpoint_opens_no_chat()
    {
        var store = await SignedIn();
        LastGcStore.Save(Paths.LastGcFile, new LastGcStore.Entry(_gcA, "ws://127.0.0.1:9000/ws"));

        Assert.Null(Resolve([], [], store, hasEndpoint: false));
        Assert.Null(Resolve([], [], store, new ServerEndpoint(new Uri("http://example.com/"))));
    }

    [Fact]
    public void A_bad_dev_gc_value_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => Resolve([LinuxChatSettings.DevGcFlag, "nope"], [], new InMemorySessionStore()));
    }

    [Fact]
    public void The_last_gc_file_is_0600_and_holds_no_token()
    {
        Assert.True(LastGcStore.Save(Paths.LastGcFile, new LastGcStore.Entry(_gcA, "ws://127.0.0.1:9000/ws")));

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Paths.LastGcFile));
        Assert.DoesNotContain("token", File.ReadAllText(Paths.LastGcFile), StringComparison.OrdinalIgnoreCase);
    }
}
