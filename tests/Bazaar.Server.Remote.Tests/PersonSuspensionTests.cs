using Bazaar.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Remote.Tests;

/// <summary>Suspending and unsuspending a person (doc 32, PROPOSED): state, device revocation, audit, and the refusal of sign-in and refresh.</summary>
public sealed class PersonSuspensionTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly PrincipalRef _admin = new(Guid.Parse("0197a000-0000-7000-8000-00000000a001"), "human");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(
        InMemoryRefreshCredentialStore Store,
        PersonSuspensionService Suspension,
        TokenService Tokens,
        FakeAuditLog Log,
        Guid HumanId,
        Guid DeviceId,
        string Secret);

    private static async Task<Harness> CreateAsync(string role = "coworker")
    {
        var time = new FakeTimeProvider(_start);
        var store = new InMemoryRefreshCredentialStore();
        var log = new FakeAuditLog();
        var audit = new PrivacyAuditRecorder(log, time);
        var humanId = Guid.CreateVersion7(_start);
        store.SetHuman(new HumanRecord(humanId, "Dana", role, "active"));
        var deviceId = Guid.CreateVersion7(_start);
        var secret = RefreshCredentialSecret.Generate();
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "phone", "android", 1, null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(secret),
            _start,
            _start.AddDays(30),
            Ct);
        var tokens = new TokenService(store, new FakeAccessTokenIssuer(), audit, Options.Create(new RemoteTokenOptions()), time);
        return new Harness(store, new PersonSuspensionService(store, audit, time), tokens, log, humanId, deviceId, secret);
    }

    [Fact]
    public async Task Suspending_sets_the_status_revokes_the_devices_and_audits_each_step()
    {
        var h = await CreateAsync();

        Assert.Equal(PersonSuspensionOutcome.Done, await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct));

        Assert.Equal("suspended", (await h.Store.FindHumanAsync(h.HumanId, Ct))!.Status);
        Assert.NotNull((await h.Store.FindDeviceAsync(h.DeviceId, Ct))!.RevokedAt);
        Assert.Equal(
            new[] { PrivacyAuditKinds.PersonSuspended, PrivacyAuditKinds.DeviceRevoked },
            h.Log.Entries.Select(e => e.Kind).ToArray());
        Assert.All(h.Log.Entries, e => Assert.Equal("human:" + _admin.Id.ToString("D"), e.Actor));
    }

    [Fact]
    public async Task A_suspended_person_cannot_refresh()
    {
        var h = await CreateAsync();
        await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct);

        var refused = await Assert.ThrowsAsync<TokenRefreshException>(
            () => h.Tokens.RefreshAsync(h.Secret, new DeviceInfo(h.DeviceId.ToString("D"), "android", "b"), Ct));

        Assert.Equal(TokenRefreshFailure.DeviceRevoked, refused.Failure);
    }

    [Fact]
    public async Task A_suspended_person_with_a_live_device_row_is_still_refused_a_refresh()
    {
        var h = await CreateAsync();
        h.Store.SetHuman(new HumanRecord(h.HumanId, "Dana", "coworker", "suspended"));

        var refused = await Assert.ThrowsAsync<TokenRefreshException>(
            () => h.Tokens.RefreshAsync(h.Secret, new DeviceInfo(h.DeviceId.ToString("D"), "android", "b"), Ct));

        Assert.Equal(TokenRefreshFailure.PersonNotActive, refused.Failure);
    }

    [Fact]
    public async Task A_suspended_person_cannot_sign_in_a_new_device()
    {
        var h = await CreateAsync();
        await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct);

        var refused = await Assert.ThrowsAsync<TokenIssueException>(
            () => h.Tokens.IssueAsync(Guid.CreateVersion7(_start), h.HumanId, new DeviceInfo("n", "linux", "b"), Ct));

        Assert.Equal(TokenIssueFailure.PersonNotActive, refused.Failure);
    }

    [Fact]
    public async Task Unsuspending_restores_sign_in_but_not_the_old_devices()
    {
        var h = await CreateAsync();
        await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct);

        Assert.Equal(PersonSuspensionOutcome.Done, await h.Suspension.UnsuspendAsync(h.HumanId, _admin, Ct));

        Assert.Equal("active", (await h.Store.FindHumanAsync(h.HumanId, Ct))!.Status);
        var old = await Assert.ThrowsAsync<TokenRefreshException>(
            () => h.Tokens.RefreshAsync(h.Secret, new DeviceInfo(h.DeviceId.ToString("D"), "android", "b"), Ct));
        Assert.Equal(TokenRefreshFailure.DeviceRevoked, old.Failure);
        var fresh = await h.Tokens.IssueAsync(Guid.CreateVersion7(_start), h.HumanId, new DeviceInfo("n", "linux", "b"), Ct);
        Assert.False(string.IsNullOrEmpty(fresh.RefreshToken));
        Assert.Equal(PrivacyAuditKinds.PersonUnsuspended, h.Log.Entries[^1].Kind);
    }

    [Fact]
    public async Task Repeating_a_change_is_a_no_op_with_no_new_audit_entry()
    {
        var h = await CreateAsync();
        await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct);
        var entries = h.Log.Entries.Count;

        Assert.Equal(PersonSuspensionOutcome.NoChange, await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct));
        await h.Suspension.UnsuspendAsync(h.HumanId, _admin, Ct);
        Assert.Equal(PersonSuspensionOutcome.NoChange, await h.Suspension.UnsuspendAsync(h.HumanId, _admin, Ct));

        Assert.Equal(entries + 1, h.Log.Entries.Count);
    }

    [Fact]
    public async Task The_owner_yourself_and_unknown_people_are_refused_without_a_write()
    {
        var owner = await CreateAsync(role: "owner");
        var coworker = await CreateAsync();

        Assert.Equal(PersonSuspensionOutcome.NotAllowed, await owner.Suspension.SuspendAsync(owner.HumanId, _admin, Ct));
        Assert.Equal(PersonSuspensionOutcome.NotAllowed, await coworker.Suspension.SuspendAsync(coworker.HumanId, new PrincipalRef(coworker.HumanId, "human"), Ct));
        Assert.Equal(PersonSuspensionOutcome.NotFound, await coworker.Suspension.SuspendAsync(Guid.NewGuid(), _admin, Ct));
        Assert.Equal(PersonSuspensionOutcome.NotFound, await coworker.Suspension.UnsuspendAsync(Guid.NewGuid(), _admin, Ct));
        Assert.Equal("active", (await owner.Store.FindHumanAsync(owner.HumanId, Ct))!.Status);
        Assert.Empty(owner.Log.Entries);
    }

    [Fact]
    public async Task A_removed_person_is_never_changed()
    {
        var h = await CreateAsync();
        h.Store.SetHuman(new HumanRecord(h.HumanId, "Dana", "coworker", "removed"));

        Assert.Equal(PersonSuspensionOutcome.NoChange, await h.Suspension.SuspendAsync(h.HumanId, _admin, Ct));
        Assert.Equal(PersonSuspensionOutcome.NoChange, await h.Suspension.UnsuspendAsync(h.HumanId, _admin, Ct));
        Assert.Equal("removed", (await h.Store.FindHumanAsync(h.HumanId, Ct))!.Status);
    }

    [Fact]
    public async Task The_ops_ports_map_outcomes_to_fixed_text_and_the_panel_session_check_follows_the_status()
    {
        var h = await CreateAsync();
        var revoker = new RemoteOpsMemberRevoker(h.Suspension);
        var restorer = new RemoteOpsMemberRestorer(h.Suspension);
        var check = new RemoteAdminRevocationCheck(h.Store);
        Assert.False(await check.IsRevokedAsync(h.HumanId, Guid.Empty, Ct));

        Assert.True((await revoker.RevokeMemberAsync(_admin.Id, h.HumanId, Ct)).Done);
        Assert.True(await check.IsRevokedAsync(h.HumanId, Guid.Empty, Ct));
        Assert.Equal("That person is not in a state that can be suspended.", (await revoker.RevokeMemberAsync(_admin.Id, h.HumanId, Ct)).Reason);
        Assert.Equal("No such person.", (await revoker.RevokeMemberAsync(_admin.Id, Guid.NewGuid(), Ct)).Reason);

        Assert.True((await restorer.RestoreMemberAsync(_admin.Id, h.HumanId, Ct)).Done);
        Assert.False(await check.IsRevokedAsync(h.HumanId, Guid.Empty, Ct));
        Assert.True(await check.IsRevokedAsync(h.HumanId, h.DeviceId, Ct));
    }
}

/// <summary>The same suspension over the real tables on a real PostgreSQL. Skipped without <c>BAZAAR_TEST_PG</c>; the <c>checkpoint</c> CI label runs it.</summary>
public sealed class PostgresPersonSuspensionTests(RemoteAuthDb db) : IClassFixture<RemoteAuthDb>
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly PrincipalRef _admin = new(Guid.Parse("0197a000-0000-7000-8000-00000000a002"), "human");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task InsertHumanAsync(Guid id, string role, string status = "active")
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Suspension test', $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<(Guid DeviceId, string Secret)> AddDeviceAsync(PostgresRefreshCredentialStore store, Guid humanId)
    {
        var deviceId = Guid.CreateVersion7(_start);
        var secret = RefreshCredentialSecret.Generate();
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "phone", "android", 1, null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(secret),
            _start,
            _start.AddDays(30),
            Ct);
        return (deviceId, secret);
    }

    private async Task<Guid> AddApprovalKeyAsync(Guid humanId, Guid deviceId)
    {
        var id = Guid.CreateVersion7(_start);
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO approval_key (id, human_id, device_id, public_key) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = [1, 2, 3] });
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<bool> KeyRevokedAsync(Guid keyId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT revoked_at IS NOT NULL FROM approval_key WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = keyId });
        return (bool)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Suspend_revokes_every_device_and_approval_key_in_one_step_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var humanId = Guid.CreateVersion7(_start);
        await InsertHumanAsync(humanId, "coworker");
        var store = new PostgresRefreshCredentialStore(db.DataSource);
        var (first, secret) = await AddDeviceAsync(store, humanId);
        var (second, _) = await AddDeviceAsync(store, humanId);
        var key = await AddApprovalKeyAsync(humanId, first);
        var time = new FakeTimeProvider(_start);
        var log = new FakeAuditLog();
        var audit = new PrivacyAuditRecorder(log, time);
        var service = new PersonSuspensionService(store, audit, time);

        Assert.Equal(PersonSuspensionOutcome.Done, await service.SuspendAsync(humanId, _admin, Ct));

        Assert.Equal("suspended", (await store.FindHumanAsync(humanId, Ct))!.Status);
        Assert.NotNull((await store.FindDeviceAsync(first, Ct))!.RevokedAt);
        Assert.NotNull((await store.FindDeviceAsync(second, Ct))!.RevokedAt);
        Assert.True(await KeyRevokedAsync(key));
        Assert.Equal(3, log.Entries.Count);
        var tokens = new TokenService(store, new FakeAccessTokenIssuer(), audit, Options.Create(new RemoteTokenOptions()), time);
        var refused = await Assert.ThrowsAsync<TokenRefreshException>(
            () => tokens.RefreshAsync(secret, new DeviceInfo(first.ToString("D"), "android", "b"), Ct));
        Assert.Equal(TokenRefreshFailure.DeviceRevoked, refused.Failure);
    }

    [Fact]
    public async Task Unsuspend_sets_active_and_leaves_devices_revoked_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var humanId = Guid.CreateVersion7(_start);
        await InsertHumanAsync(humanId, "coworker");
        var store = new PostgresRefreshCredentialStore(db.DataSource);
        var (device, _) = await AddDeviceAsync(store, humanId);
        var time = new FakeTimeProvider(_start);
        var service = new PersonSuspensionService(store, new PrivacyAuditRecorder(new FakeAuditLog(), time), time);
        await service.SuspendAsync(humanId, _admin, Ct);

        Assert.Equal(PersonSuspensionOutcome.Done, await service.UnsuspendAsync(humanId, _admin, Ct));
        Assert.Equal(PersonSuspensionOutcome.NoChange, await service.UnsuspendAsync(humanId, _admin, Ct));

        Assert.Equal("active", (await store.FindHumanAsync(humanId, Ct))!.Status);
        Assert.NotNull((await store.FindDeviceAsync(device, Ct))!.RevokedAt);
    }

    [Fact]
    public async Task The_owner_and_a_removed_person_are_not_touched_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var removed = Guid.CreateVersion7(_start);
        await InsertHumanAsync(removed, "coworker", "removed");
        var store = new PostgresRefreshCredentialStore(db.DataSource);
        var time = new FakeTimeProvider(_start);

        Assert.False((await store.SuspendHumanAsync(removed, _start, Ct)).Changed);
        Assert.False(await store.UnsuspendHumanAsync(removed, Ct));
        Assert.Equal("removed", (await store.FindHumanAsync(removed, Ct))!.Status);

        // The owner row is unique per database, so the guard is checked through the service only when no owner exists yet.
        var owner = (await store.ListHumansAsync(Ct)).FirstOrDefault(h => h.Role == "owner");
        if (owner is null)
        {
            var id = Guid.CreateVersion7(_start);
            await InsertHumanAsync(id, "owner");
            owner = await store.FindHumanAsync(id, Ct);
        }

        Assert.False((await store.SuspendHumanAsync(owner!.Id, _start, Ct)).Changed);
        Assert.Equal("active", (await store.FindHumanAsync(owner.Id, Ct))!.Status);
        _ = time;
    }
}
