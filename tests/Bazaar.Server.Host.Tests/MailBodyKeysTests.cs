using System.Security.Cryptography;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;

namespace Bazaar.Server.Host.Tests;

public sealed class MailBodyKeysTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private static readonly byte[] _dataBytes = "hello from a test message, subject Q3 plan"u8.ToArray();

    // Database-free stand-in for the per-person key store: keys per person, destroyable.
    private sealed class MemoryKeys : IMailPersonKeys
    {
        private readonly Dictionary<Guid, (Guid KeyId, byte[] Data)> _active = [];
        private readonly Dictionary<Guid, byte[]> _all = [];
        private readonly HashSet<Guid> _destroyed = [];

        public ValueTask<PersonKeyStore.PersonKey> GetOrCreateActiveAsync(Guid personId, CancellationToken ct)
        {
            if (!_active.TryGetValue(personId, out var entry))
            {
                entry = (Guid.CreateVersion7(), RandomNumberGenerator.GetBytes(32));
                _active[personId] = entry;
                _all[entry.KeyId] = entry.Data;
            }

            return ValueTask.FromResult(new PersonKeyStore.PersonKey(entry.KeyId, (byte[])entry.Data.Clone()));
        }

        public ValueTask<byte[]?> UnwrapAsync(Guid keyId, CancellationToken ct)
        {
            if (!_all.TryGetValue(keyId, out var data)) throw new InvalidOperationException("unknown key");
            return ValueTask.FromResult(_destroyed.Contains(keyId) ? null : (byte[]?)data.Clone());
        }

        public void Destroy(Guid keyId) => _destroyed.Add(keyId);
    }

    [Fact]
    public async Task Protector_round_trips_and_stores_no_plaintext()
    {
        var protector = new PersonKeyMailBodyProtector(new MemoryKeys());
        var body = await protector.ProtectAsync(Guid.NewGuid().ToString(), _dataBytes, _ct);

        Assert.Equal(_dataBytes.Length + 16, body.Ciphertext.Length);
        Assert.Equal(12, body.Nonce.Length);
        Assert.False(body.Ciphertext.AsSpan().IndexOf(_dataBytes.AsSpan(0, 16)) >= 0);
        Assert.Equal(_dataBytes, await protector.UnprotectAsync(body, _ct));
    }

    [Fact]
    public async Task Protector_uses_a_fresh_nonce_each_time()
    {
        var protector = new PersonKeyMailBodyProtector(new MemoryKeys());
        var agent = Guid.NewGuid().ToString();
        var a = await protector.ProtectAsync(agent, _dataBytes, _ct);
        var b = await protector.ProtectAsync(agent, _dataBytes, _ct);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.Equal(a.KeyId, b.KeyId);
    }

    [Fact]
    public async Task Protector_refuses_an_agent_id_that_is_not_a_principal_id()
    {
        var protector = new PersonKeyMailBodyProtector(new MemoryKeys());
        await Assert.ThrowsAsync<ArgumentException>(async () => await protector.ProtectAsync("charles", _dataBytes, _ct));
    }

    [Fact]
    public async Task Destroyed_key_fails_closed_for_protector_and_sealer()
    {
        var keys = new MemoryKeys();
        var protector = new PersonKeyMailBodyProtector(keys);
        var sealer = new PersonKeyMailBodySealer(keys);
        var person = Guid.NewGuid();

        var protectedBody = await protector.ProtectAsync(person.ToString(), _dataBytes, _ct);
        var sealedBody = await sealer.SealAsync(person, _dataBytes, _ct);
        keys.Destroy(protectedBody.KeyId);

        var e1 = await Assert.ThrowsAsync<MailBodyErasedException>(async () => await protector.UnprotectAsync(protectedBody, _ct));
        var e2 = await Assert.ThrowsAsync<MailBodyErasedException>(async () => await sealer.OpenAsync(person, sealedBody, _ct));
        Assert.Equal(protectedBody.KeyId, e1.KeyId);
        Assert.Equal(sealedBody.KeyId, e2.KeyId);
    }

    [Fact]
    public async Task Tampered_body_fails_for_both_ports()
    {
        var keys = new MemoryKeys();
        var protector = new PersonKeyMailBodyProtector(keys);
        var sealer = new PersonKeyMailBodySealer(keys);
        var person = Guid.NewGuid();

        var body = await protector.ProtectAsync(person.ToString(), _dataBytes, _ct);
        var truncated = new ProtectedBody(body.KeyId, body.Ciphertext[..4], body.Nonce);
        body.Ciphertext[0] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await protector.UnprotectAsync(body, _ct));

        var sealedBody = await sealer.SealAsync(person, _dataBytes, _ct);
        sealedBody.Ciphertext[^1] ^= 1;
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await sealer.OpenAsync(person, sealedBody, _ct));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await protector.UnprotectAsync(truncated, _ct));
    }

    [Fact]
    public async Task Wrong_associated_data_fails()
    {
        var keys = new MemoryKeys();
        var protector = new PersonKeyMailBodyProtector(keys);
        var sealer = new PersonKeyMailBodySealer(keys);
        var person = Guid.NewGuid();
        var sealedBody = await sealer.SealAsync(person, _dataBytes, _ct);

        // Another person changes the associated data.
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await sealer.OpenAsync(Guid.NewGuid(), sealedBody, _ct));

        // A body presented under another person's key id changes it too.
        var mine = await protector.ProtectAsync(person.ToString(), _dataBytes, _ct);
        var theirs = await protector.ProtectAsync(Guid.NewGuid().ToString(), _dataBytes, _ct);
        var swapped = new ProtectedBody(theirs.KeyId, mine.Ciphertext, mine.Nonce);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await protector.UnprotectAsync(swapped, _ct));
    }

    [Fact]
    public async Task Sealer_round_trips_and_stores_no_plaintext()
    {
        var sealer = new PersonKeyMailBodySealer(new MemoryKeys());
        var person = Guid.NewGuid();
        var sealedBody = await sealer.SealAsync(person, _dataBytes, _ct);

        Assert.False(sealedBody.Ciphertext.AsSpan().IndexOf(_dataBytes.AsSpan(0, 16)) >= 0);
        Assert.Equal(_dataBytes, await sealer.OpenAsync(person, sealedBody, _ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await sealer.SealAsync(Guid.Empty, _dataBytes, _ct));
    }
}
