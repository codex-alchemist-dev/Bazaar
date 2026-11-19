using System.Collections.Concurrent;
using Bazaar.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Remote.Tests;

/// <summary>Parallel refreshes of one credential (doc 14): at most one rotation succeeds, on both stores.</summary>
public sealed class RefreshRotationRaceTests(RemoteAuthDb db) : IClassFixture<RemoteAuthDb>
{
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SafeIssuer : IAccessTokenIssuer
    {
        public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt) => "access-token-under-test";
    }

    private sealed class SafeAuditLog : IAuditLog
    {
        public ConcurrentQueue<AuditEntry> Entries { get; } = new();

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Enqueue(e);
            return Task.CompletedTask;
        }
    }

    private async Task<Guid> InsertHumanAsync()
    {
        var humanId = Guid.CreateVersion7(_start);
        await using var insert = db.DataSource.CreateCommand(
            "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Test coworker', 'coworker', 'active')");
        insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        await insert.ExecuteNonQueryAsync(Ct);
        return humanId;
    }

    private static async Task RunRaceAsync(IRefreshCredentialStore store, Guid humanId, SafeAuditLog log)
    {
        var time = new FakeTimeProvider(_start);
        var deviceId = Guid.CreateVersion7(_start);
        var secret = RefreshCredentialSecret.Generate();
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "racer", "android", Revision: 1, RevokedAt: null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(secret),
            _start,
            _start.AddDays(30),
            Ct);
        var service = new TokenService(store, new SafeIssuer(), new PrivacyAuditRecorder(log, time), Options.Create(new RemoteTokenOptions()), time);
        var device = new DeviceInfo(deviceId.ToString("D"), "android", "test-build");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            try
            {
                return (Pair: (TokenPair?)await service.RefreshAsync(secret, device, Ct), Failure: (TokenRefreshFailure?)null);
            }
            catch (TokenRefreshException ex)
            {
                return (Pair: (TokenPair?)null, Failure: (TokenRefreshFailure?)ex.Failure);
            }
        }, Ct)));

        var winners = outcomes.Where(o => o.Pair is not null).ToList();
        Assert.Single(winners);
        Assert.All(outcomes.Where(o => o.Pair is null), o => Assert.True(o.Failure is TokenRefreshFailure.ReuseDetected or TokenRefreshFailure.DeviceRevoked));

        // Exactly one retired credential and at most one new live one for the device chain.
        var current = await store.FindByHashAsync(RefreshCredentialSecret.Hash(winners[0].Pair!.RefreshToken), Ct);
        Assert.NotNull(current);
        Assert.Null(current.RetiredAt);
        var original = await store.FindByHashAsync(RefreshCredentialSecret.Hash(secret), Ct);
        Assert.NotNull(original!.RetiredAt);
        Assert.Equal(1, log.Entries.Count(e => e.Kind == PrivacyAuditKinds.DeviceRevoked));
    }

    [Fact]
    public async Task Twelve_parallel_refreshes_of_one_credential_rotate_once_in_memory()
    {
        await RunRaceAsync(new InMemoryRefreshCredentialStore(), Guid.CreateVersion7(_start), new SafeAuditLog());
    }

    [Fact]
    public async Task Twelve_parallel_refreshes_of_one_credential_rotate_once_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        await RunRaceAsync(new PostgresRefreshCredentialStore(db.DataSource), await InsertHumanAsync(), new SafeAuditLog());
    }
}
