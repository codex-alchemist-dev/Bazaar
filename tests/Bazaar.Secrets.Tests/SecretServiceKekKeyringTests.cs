using System.Security.Cryptography;
using Bazaar.Secrets.Keyring;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Secrets.Tests;

/// <summary>Doc 61 Q2: the libsecret keyring that seals the <c>file</c> KEK provider's key file.</summary>
public sealed class SecretServiceKekKeyringTests
{
    private static readonly byte[] _aad = "bazaar.kek.file.v1|libsecret|e|file-kek-v1"u8.ToArray();

    [Fact]
    public async Task Seal_creates_one_entry_and_opens_after_a_restart()
    {
        var tool = new FakeSecretTool();
        var key = RandomNumberGenerator.GetBytes(32);

        var sealedKey = await new SecretServiceKekKeyring(tool).SealAsync("e", key, _aad, CancellationToken.None);
        var opened = await new SecretServiceKekKeyring(tool).OpenAsync("e", sealedKey, _aad, CancellationToken.None);

        Assert.Equal(key, opened);
        Assert.Equal(2, tool.StoreCount);
        Assert.False(sealedKey.AsSpan().IndexOf(key) >= 0);
        Assert.DoesNotContain(Convert.ToBase64String(key), tool.Items["e"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_without_the_entry_or_with_other_associated_data_fails_closed()
    {
        var tool = new FakeSecretTool();
        var keyring = new SecretServiceKekKeyring(tool);
        var sealedKey = await keyring.SealAsync("e", new byte[32], _aad, CancellationToken.None);

        await Assert.ThrowsAsync<KekKeyringException>(() => keyring.OpenAsync("other", sealedKey, _aad, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<KekKeyringException>(() => keyring.OpenAsync("e", sealedKey, "x"u8.ToArray(), CancellationToken.None).AsTask());
        Assert.Equal(2, tool.StoreCount);
    }

    [Fact]
    public async Task Probe_reports_entry_state_and_unavailability()
    {
        var tool = new FakeSecretTool();
        var keyring = new SecretServiceKekKeyring(tool);

        Assert.Equal(new KekKeyringProbe(true, false, null), await keyring.ProbeAsync("e", CancellationToken.None));
        await keyring.SealAsync("e", new byte[32], _aad, CancellationToken.None);
        Assert.Equal(new KekKeyringProbe(true, true, null), await keyring.ProbeAsync("e", CancellationToken.None));

        var locked = new SecretServiceKekKeyring(new FakeSecretTool { LookupResult = new SecretToolResult(1, "", "Cannot autolaunch D-Bus without X11 $DISPLAY") });
        Assert.False((await locked.ProbeAsync("e", CancellationToken.None)).Available);

        var missing = new SecretServiceKekKeyring(new FakeSecretTool { Throw = new KekCustodyException("secret-tool is not installed.") });
        var probe = await missing.ProbeAsync("e", CancellationToken.None);
        Assert.False(probe.Available);
        Assert.Contains("not installed", probe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_commitment_matches_only_the_key_that_was_sealed()
    {
        var tool = new FakeSecretTool();
        var keyring = new SecretServiceKekKeyring(tool);
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.False(await keyring.VerifyCommitmentAsync("e", key, CancellationToken.None));

        await keyring.SealAsync("e", key, _aad, CancellationToken.None);

        Assert.True(await keyring.VerifyCommitmentAsync("e", key, CancellationToken.None));
        Assert.False(await keyring.VerifyCommitmentAsync("e", RandomNumberGenerator.GetBytes(32), CancellationToken.None));
        Assert.False(await keyring.VerifyCommitmentAsync("other", key, CancellationToken.None));
        Assert.DoesNotContain(Convert.ToBase64String(key), tool.Items["bazaar-kek-file-commit/e"], StringComparison.Ordinal);
        tool.Items.Remove("bazaar-kek-file-commit/e");
        Assert.False(await keyring.VerifyCommitmentAsync("e", key, CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_store_or_unclear_lookup_never_creates_a_key()
    {
        var refused = new SecretServiceKekKeyring(new FakeSecretTool { StoreResult = new SecretToolResult(1, "", "locked") });
        await Assert.ThrowsAsync<KekKeyringException>(() => refused.SealAsync("e", new byte[32], _aad, CancellationToken.None).AsTask());

        var unclear = new FakeSecretTool { LookupResult = new SecretToolResult(2, "", "locked collection") };
        await Assert.ThrowsAsync<KekKeyringException>(() => new SecretServiceKekKeyring(unclear).SealAsync("e", new byte[32], _aad, CancellationToken.None).AsTask());
        Assert.Equal(0, unclear.StoreCount);
    }

    private sealed class FakeSecretTool : ISecretToolRunner
    {
        public Dictionary<string, string> Items { get; } = [];

        public SecretToolResult? LookupResult { get; init; }

        public SecretToolResult? StoreResult { get; init; }

        public Exception? Throw { get; init; }

        public int StoreCount { get; private set; }

        public Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
        {
            if (Throw is not null)
                throw Throw;
            var account = arguments[^1];
            var service = arguments[arguments.ToList().IndexOf("service") + 1];
            var itemKey = service == "bazaar-kek-file" ? account : service + "/" + account;
            if (arguments[0] == "lookup")
            {
                if (LookupResult is not null)
                    return Task.FromResult(LookupResult);
                return Task.FromResult(Items.TryGetValue(itemKey, out var value) ? new SecretToolResult(0, value, "") : new SecretToolResult(1, "", ""));
            }

            StoreCount++;
            if (StoreResult is not null)
                return Task.FromResult(StoreResult);
            Items[itemKey] = standardInput!;
            return Task.FromResult(new SecretToolResult(0, "", ""));
        }
    }
}
