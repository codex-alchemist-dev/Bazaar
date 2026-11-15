using System.Text;
using Bazaar.Secrets.Keyring;

namespace Bazaar.Secrets.Tests;

public sealed class SecretServiceKekProviderTests
{
    private static readonly byte[] _aad = Encoding.UTF8.GetBytes("bazaar.secrets.datakey.v1|installation|kek");

    [Fact]
    public async Task FirstOpenCreatesKeyAndLaterOpenReusesIt()
    {
        var keyring = new FakeSecretTool();

        using var first = await SecretServiceKekProvider.OpenAsync(keyring, "kek-1", CancellationToken.None);
        var wrapped = await first.WrapAsync(new byte[] { 1, 2, 3 }, _aad, CancellationToken.None);
        using var second = await SecretServiceKekProvider.OpenAsync(keyring, "kek-1", CancellationToken.None);
        var unwrapped = await second.UnwrapAsync("kek-1", wrapped, _aad, CancellationToken.None);

        Assert.Equal(1, keyring.StoreCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, unwrapped.ToArray());
    }

    [Fact]
    public async Task UnavailableKeyringThrowsAndCreatesNothing()
    {
        var keyring = new FakeSecretTool { LookupResult = new SecretToolResult(1, "", "Object does not exist at path /org/freedesktop/secrets/collection/login") };

        await Assert.ThrowsAsync<KekCustodyException>(() => SecretServiceKekProvider.OpenAsync(keyring, "kek-1", CancellationToken.None));
        Assert.Equal(0, keyring.StoreCount);
    }

    [Fact]
    public async Task MalformedStoredKeyThrowsInsteadOfReplacingIt()
    {
        var keyring = new FakeSecretTool();
        keyring.Items["kek-1"] = Convert.ToBase64String(new byte[16]);

        await Assert.ThrowsAsync<KekCustodyException>(() => SecretServiceKekProvider.OpenAsync(keyring, "kek-1", CancellationToken.None));
        Assert.Equal(0, keyring.StoreCount);
    }

    [Fact]
    public async Task StoreFailureThrows()
    {
        var keyring = new FakeSecretTool { StoreResult = new SecretToolResult(1, "", "locked") };

        await Assert.ThrowsAsync<KekCustodyException>(() => SecretServiceKekProvider.OpenAsync(keyring, "kek-1", CancellationToken.None));
    }

    [Fact]
    public async Task NonBase64ItemThrows()
    {
        var keyring = new FakeSecretTool();
        keyring.Items["kek-1"] = "not base64!!";

        await Assert.ThrowsAsync<KekCustodyException>(() => SecretServiceKekProvider.OpenAsync(keyring, "kek-1", CancellationToken.None));
    }

    /// <summary>
    /// Runs against the real Secret Service through <c>secret-tool</c>. Set BAZAAR_TEST_KEYRING=1 inside a session that has an
    /// unlocked keyring (for example: dbus-run-session -- sh -c 'echo -n pw | gnome-keyring-daemon --unlock --components=secrets; ...').
    /// The item is removed afterwards.
    /// </summary>
    [Fact]
    public async Task LiveKeyringKeepsTheSameKekAcrossOpens()
    {
        if (Environment.GetEnvironmentVariable("BAZAAR_TEST_KEYRING") != "1")
        {
            Assert.Skip("Set BAZAAR_TEST_KEYRING=1 under an unlocked Secret Service session to run this test.");
        }

        var runner = new ProcessSecretToolRunner();
        var keyId = "live-test-" + Guid.NewGuid().ToString("N");
        try
        {
            using var first = await SecretServiceKekProvider.OpenAsync(runner, keyId, CancellationToken.None);
            var wrapped = await first.WrapAsync(new byte[] { 9, 8, 7 }, _aad, CancellationToken.None);
            using var second = await SecretServiceKekProvider.OpenAsync(runner, keyId, CancellationToken.None);
            var unwrapped = await second.UnwrapAsync(keyId, wrapped, _aad, CancellationToken.None);

            Assert.Equal(new byte[] { 9, 8, 7 }, unwrapped.ToArray());
        }
        finally
        {
            await runner.RunAsync(["clear", "service", "bazaar-kek", "account", keyId], null, CancellationToken.None);
        }
    }

    /// <summary>In-memory stand-in for secret-tool lookup and store, with the exit codes the real tool uses.</summary>
    private sealed class FakeSecretTool : ISecretToolRunner
    {
        public Dictionary<string, string> Items { get; } = [];

        public SecretToolResult? LookupResult { get; init; }

        public SecretToolResult? StoreResult { get; init; }

        public int StoreCount { get; private set; }

        public Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
        {
            var account = arguments[^1];
            if (arguments[0] == "lookup")
            {
                if (LookupResult is not null)
                {
                    return Task.FromResult(LookupResult);
                }

                return Task.FromResult(Items.TryGetValue(account, out var value)
                    ? new SecretToolResult(0, value, "")
                    : new SecretToolResult(1, "", ""));
            }

            StoreCount++;
            if (StoreResult is not null)
            {
                return Task.FromResult(StoreResult);
            }

            Items[account] = standardInput!;
            return Task.FromResult(new SecretToolResult(0, "", ""));
        }
    }
}
