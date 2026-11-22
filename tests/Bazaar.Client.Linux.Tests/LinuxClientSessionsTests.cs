using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Bazaar.Client.Chat;
using Bazaar.Client.Linux.Secrets;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Linux.Tests;

internal static class SessionTestSetup
{
    // No test in this assembly may reach the real keyring or the real state directory.
    [ModuleInitializer]
    internal static void Init() => LinuxClientSessions.Override = new InMemorySessionStore();
}

public sealed class LinuxClientSessionsTests : IDisposable
{
    private const string _secretMarker = "tok-SECRET-marker";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-linux-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientSession Session(DateTimeOffset? expires = null) =>
        new(_secretMarker, "refresh-SECRET-marker", expires ?? DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task Keyring_round_trip_survives_a_new_store_and_never_puts_secrets_in_arguments()
    {
        var runner = new FakeRunner();
        var first = LinuxClientSessions.Select(runner, new LinuxPaths(_dir), _ => { });
        Assert.Equal(SessionKeyCustody.Libsecret, first.Custody);
        await first.Store.SaveAsync(Session(), Ct);

        var second = LinuxClientSessions.Select(runner, new LinuxPaths(_dir), _ => { });
        var loaded = await second.Store.LoadAsync(Ct);

        Assert.Equal(_secretMarker, loaded?.AccessToken);
        Assert.False(File.Exists(Path.Combine(_dir, "session.key")));
        Assert.All(runner.Calls, call => Assert.DoesNotContain(call.Arguments, a => a.Contains(runner.StoredSecret ?? "-none-", StringComparison.Ordinal)));
        Assert.Contains(runner.Calls, c => c.Arguments[0] == "store" && c.Input == runner.StoredSecret);
        Assert.DoesNotContain(_secretMarker, await File.ReadAllTextAsync(Path.Combine(_dir, "session.bin"), Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Falls_back_to_the_key_file_when_the_keyring_is_missing_or_refuses(bool toolMissing)
    {
        var runner = new FakeRunner { Missing = toolMissing, StoreExit = 1 };
        var reports = new List<string>();
        var selection = LinuxClientSessions.Select(runner, new LinuxPaths(_dir), reports.Add);

        Assert.Equal(SessionKeyCustody.File, selection.Custody);
        Assert.Contains(reports, r => r.Contains("no keyring", StringComparison.Ordinal));
        await selection.Store.SaveAsync(Session(), Ct);
        var again = LinuxClientSessions.Select(runner, new LinuxPaths(_dir), _ => { });
        Assert.Equal(_secretMarker, (await again.Store.LoadAsync(Ct))?.AccessToken);
        Assert.All(reports, r => Assert.DoesNotContain("SECRET", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_file_and_session_file_are_0600_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;

        var selection = LinuxClientSessions.Select(new FakeRunner { Missing = true }, new LinuxPaths(_dir), _ => { });
        await selection.Store.SaveAsync(Session(), Ct);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(Path.Combine(_dir, "session.key")));
        Assert.Equal(expected, File.GetUnixFileMode(Path.Combine(_dir, "session.bin")));
    }

    [Fact]
    public void A_wrong_key_refuses_and_tampering_fails_closed()
    {
        var one = new LinuxSecretProtector(RandomNumberGenerator.GetBytes(32), SessionKeyCustody.File);
        var other = new LinuxSecretProtector(RandomNumberGenerator.GetBytes(32), SessionKeyCustody.File);
        var sealedBytes = one.Protect("hello"u8);

        Assert.Equal("hello"u8.ToArray(), one.Unprotect(sealedBytes));
        Assert.Null(other.Unprotect(sealedBytes));

        var flipped = (byte[])sealedBytes.Clone();
        flipped[^1] ^= 1;
        Assert.Null(one.Unprotect(flipped));
        Assert.Null(one.Unprotect(sealedBytes.AsSpan(0, 10)));
        Assert.Null(one.Unprotect([]));
    }

    [Fact]
    public async Task A_corrupt_session_file_reads_as_signed_out_and_is_removed()
    {
        var selection = LinuxClientSessions.Select(new FakeRunner(), new LinuxPaths(_dir), _ => { });
        var path = Path.Combine(_dir, "session.bin");
        await selection.Store.SaveAsync(Session(), Ct);
        await File.WriteAllBytesAsync(path, "not a sealed session at all, just junk bytes"u8.ToArray(), Ct);

        Assert.Null(await selection.Store.LoadAsync(Ct));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_unusable_keyring_and_unwritable_key_path_keep_the_session_in_memory()
    {
        var blocker = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "a file where a directory should be");

        var selection = LinuxClientSessions.Select(new FakeRunner { Missing = true }, new LinuxPaths(Path.Combine(blocker, "state")), _ => { });

        Assert.Null(selection.Custody);
        Assert.IsType<InMemorySessionStore>(selection.Store);
    }

    [Fact]
    public async Task Env_token_is_filled_from_a_valid_saved_session_and_env_wins()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(Session(), Ct);
        var env = new Dictionary<string, string>
        {
            [ChatConnectionSettings.ServerVariable] = "wss://example.test/ws",
            [ChatConnectionSettings.GcVariable] = Guid.NewGuid().ToString(),
        };
        string? Read(string n) => env.GetValueOrDefault(n);

        var settings = ChatConnectionSettings.FromEnvironment(LinuxClientSessions.EnvWithStoredToken(Read, () => store, TimeProvider.System, _ => Task.FromResult(false)));
        Assert.Equal(_secretMarker, settings?.Token);

        env[ChatConnectionSettings.TokenVariable] = "from-env";
        settings = ChatConnectionSettings.FromEnvironment(LinuxClientSessions.EnvWithStoredToken(Read, () => store, TimeProvider.System, _ => Task.FromResult(false)));
        Assert.Equal("from-env", settings?.Token);
    }

    [Fact]
    public async Task Expired_session_is_refreshed_once_and_a_failed_refresh_leaves_the_token_unset()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(Session(DateTimeOffset.UtcNow.AddMinutes(-5)), Ct);
        var env = new Dictionary<string, string>
        {
            [ChatConnectionSettings.ServerVariable] = "wss://example.test/ws",
            [ChatConnectionSettings.GcVariable] = Guid.NewGuid().ToString(),
        };

        var failing = LinuxClientSessions.EnvWithStoredToken(env.GetValueOrDefault, () => store, TimeProvider.System, _ => Task.FromResult(false));
        Assert.Throws<ArgumentException>(() => ChatConnectionSettings.FromEnvironment(failing));

        var refreshed = LinuxClientSessions.EnvWithStoredToken(env.GetValueOrDefault, () => store, TimeProvider.System, async s =>
        {
            await s.SaveAsync(new ClientSession("fresh-token", "r", DateTimeOffset.UtcNow.AddHours(1)), Ct);
            return true;
        });
        Assert.Equal("fresh-token", ChatConnectionSettings.FromEnvironment(refreshed)?.Token);
    }

    [Fact]
    public async Task Auto_sign_in_is_offered_only_with_no_settings_no_session_and_no_first_run_switch()
    {
        var empty = new InMemorySessionStore();
        Assert.Equal(["--sign-in"], LinuxClientSessions.WithAutoSignIn([], true, false, empty));
        Assert.Empty(LinuxClientSessions.WithAutoSignIn([], false, false, empty));
        Assert.Empty(LinuxClientSessions.WithAutoSignIn([], true, true, empty));
        Assert.Equal(["--create-company"], LinuxClientSessions.WithAutoSignIn(["--create-company"], true, false, empty));

        var saved = new InMemorySessionStore();
        await saved.SaveAsync(Session(), Ct);
        Assert.Empty(LinuxClientSessions.WithAutoSignIn([], true, false, saved));
    }

    private sealed record Call(IReadOnlyList<string> Arguments, string? Input);

    private sealed class FakeRunner : ISecretToolRunner
    {
        public bool Missing { get; init; }

        public int StoreExit { get; init; }

        public List<Call> Calls { get; } = [];

        public string? StoredSecret { get; private set; }

        public Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
        {
            if (Missing) throw new SecretStoreUnavailableException("secret-tool is not installed.");
            Calls.Add(new Call(arguments.ToArray(), standardInput));
            if (arguments[0] == "lookup") return Task.FromResult(new SecretToolResult(StoredSecret is null ? 1 : 0, StoredSecret ?? ""));
            if (StoreExit == 0) StoredSecret = standardInput;
            return Task.FromResult(new SecretToolResult(StoreExit, ""));
        }
    }
}
