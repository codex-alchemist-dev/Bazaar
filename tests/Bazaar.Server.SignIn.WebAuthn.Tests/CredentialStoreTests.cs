using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

/// <summary>The store contract, run against the in-memory seam always and against Postgres when <c>BAZAAR_TEST_PG</c> is set.</summary>
public abstract class CredentialStoreContract
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract IPasskeyCredentialStore CreateStore();

    private static StoredPasskeyCredential New(Guid principal, byte[]? credentialId = null, long signCount = 0) =>
        new(Guid.CreateVersion7(), principal, Guid.CreateVersion7(), credentialId ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), [1, 2, 3], signCount, "laptop", _now);

    [Fact]
    public async Task Add_then_find_round_trips_public_material_only()
    {
        var store = CreateStore();
        var c = New(Guid.NewGuid());

        Assert.True(await store.AddAsync(c, Ct));
        var found = await store.FindByCredentialIdAsync(c.CredentialId, Ct);

        Assert.NotNull(found);
        Assert.Equal(c.Id, found.Id);
        Assert.Equal(c.PrincipalId, found.PrincipalId);
        Assert.Equal(c.DeviceId, found.DeviceId);
        Assert.Equal(c.PublicKey, found.PublicKey);
        Assert.Equal("laptop", found.Label);
        Assert.Null(await store.FindByCredentialIdAsync(new byte[32], Ct));
    }

    [Fact]
    public async Task A_duplicate_credential_id_is_refused()
    {
        var store = CreateStore();
        var first = New(Guid.NewGuid());

        Assert.True(await store.AddAsync(first, Ct));
        Assert.False(await store.AddAsync(New(Guid.NewGuid(), first.CredentialId), Ct));
    }

    [Fact]
    public async Task Sign_count_advance_is_compare_and_set()
    {
        var store = CreateStore();
        var c = New(Guid.NewGuid(), signCount: 3);
        await store.AddAsync(c, Ct);

        Assert.True(await store.TryAdvanceSignCountAsync(c.Id, 3, 7, _now, Ct));
        Assert.False(await store.TryAdvanceSignCountAsync(c.Id, 3, 8, _now, Ct));
        Assert.Equal(7, (await store.FindByCredentialIdAsync(c.CredentialId, Ct))!.SignCount);
    }

    [Fact]
    public async Task Revoked_credentials_are_not_found_and_cannot_advance()
    {
        var store = CreateStore();
        var c = New(Guid.NewGuid());
        await store.AddAsync(c, Ct);

        Assert.True(await store.RevokeAsync(c.Id, _now, Ct));
        Assert.False(await store.RevokeAsync(c.Id, _now, Ct));
        Assert.Null(await store.FindByCredentialIdAsync(c.CredentialId, Ct));
        Assert.False(await store.TryAdvanceSignCountAsync(c.Id, 0, 1, _now, Ct));
    }

    [Fact]
    public async Task Count_and_list_cover_live_credentials_of_one_principal()
    {
        var store = CreateStore();
        var principal = Guid.NewGuid();
        var a = New(principal);
        var b = New(principal);
        await store.AddAsync(a, Ct);
        await store.AddAsync(b, Ct);
        await store.AddAsync(New(Guid.NewGuid()), Ct);
        await store.RevokeAsync(b.Id, _now, Ct);

        Assert.Equal(1, await store.CountAsync(principal, Ct));
        Assert.Equal([a.CredentialId], await store.ListCredentialIdsAsync(principal, Ct));
    }
}

public sealed class InMemoryCredentialStoreTests : CredentialStoreContract
{
    protected override IPasskeyCredentialStore CreateStore() => new InMemoryPasskeyCredentialStore();
}

/// <summary>A cloned database with the component's table. Runs only when <c>BAZAAR_TEST_PG</c> is set; otherwise the tests skip.</summary>
public sealed class WebAuthnDb : PgFixture, IAsyncLifetime
{
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    public override async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await base.InitializeAsync();
        var assembly = typeof(PostgresPasskeyCredentialStore).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("sign_in_webauthn.0001_webauthn_credential.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class PostgresCredentialStoreTests(WebAuthnDb db) : CredentialStoreContract, IClassFixture<WebAuthnDb>
{
    protected override IPasskeyCredentialStore CreateStore()
    {
        Assert.SkipUnless(WebAuthnDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        return new PostgresPasskeyCredentialStore(db.DataSource);
    }
}
