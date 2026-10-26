using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Souls;

/// <summary>Soul version chain against real PostgreSQL (docs/design/07-soul-system.md, Testing).</summary>
public sealed class SoulVersioningServiceTests(SoulDb db) : IClassFixture<SoulDb>
{
    private const string _charterA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string _charterB = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly int[] _threeVersions = [1, 2, 3];
    private const string _prefs = "{\"tone\":\"dry\",\"verbosity\":2}";

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private SoulVersioningService Service() => new(db.DataSource);

    private static SoulVersionAppend Append(Guid soulId, int expected, string charter = _charterA, string prefs = _prefs, string reason = "test") =>
        new(soulId, expected, charter, prefs, "owner:dom", reason);

    [Fact]
    public async Task Appending_at_expected_zero_creates_version_one_without_a_parent()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        var v1 = await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);

        Assert.Equal(1, v1.Version);
        Assert.Null(v1.ParentVersion);
        Assert.Equal(_charterA, v1.CharterSha256);
        Assert.Equal("{\"tone\":\"dry\",\"verbosity\":2}", v1.PreferencesJson);
        Assert.Equal(1, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_chain_links_each_version_to_its_parent()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);
        var v2 = await service.AppendAsync(Append(soulId, 1, charter: _charterB, reason: "tone change"), TestContext.Current.CancellationToken);
        var v3 = await service.AppendAsync(Append(soulId, 2, reason: "back"), TestContext.Current.CancellationToken);

        Assert.Equal(1, v2.ParentVersion);
        Assert.Equal(3, v3.Version);
        Assert.Equal(2, v3.ParentVersion);
        Assert.Equal(3, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
        Assert.Equal(_threeVersions, (await service.ListAsync(soulId, TestContext.Current.CancellationToken)).Select(v => v.Version));
    }

    [Fact]
    public async Task A_stale_expected_version_is_refused_and_writes_nothing()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();
        await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);
        await service.AppendAsync(Append(soulId, 1, charter: _charterB), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<SoulVersionConflictException>(() => service.AppendAsync(Append(soulId, 1), TestContext.Current.CancellationToken));

        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal(2, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await service.ListAsync(soulId, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task An_expected_version_ahead_of_the_head_is_refused_so_no_gap_can_appear()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();
        await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SoulVersionConflictException>(() => service.AppendAsync(Append(soulId, 5), TestContext.Current.CancellationToken));

        Assert.Equal(1, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creating_the_same_soul_twice_is_refused_for_the_second_writer()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();
        await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SoulVersionConflictException>(() => service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sixteen_writers_racing_to_create_one_soul_produce_exactly_one_version_one()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => TryAppendAsync(service, Append(soulId, 0, reason: $"writer {i}"))));

        Assert.Equal(1, outcomes.Count(o => o is not null));
        Assert.Equal(15, outcomes.Count(o => o is null));
        Assert.Equal(1, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
        var versions = await service.ListAsync(soulId, TestContext.Current.CancellationToken);
        Assert.Single(versions);
        Assert.Equal(1, versions[0].Version);
    }

    [Fact]
    public async Task Concurrent_writers_that_retry_on_conflict_never_fork_the_chain()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();
        await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);

        const int writers = 8;
        const int appendsEach = 5;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => WriterAsync(service, soulId, w, appendsEach)));

        var versions = await service.ListAsync(soulId, TestContext.Current.CancellationToken);
        Assert.Equal(1 + writers * appendsEach, versions.Count);
        for (var i = 0; i < versions.Count; i++)
        {
            Assert.Equal(i + 1, versions[i].Version);
            Assert.Equal(i == 0 ? null : (int?)i, versions[i].ParentVersion);
        }
    }

    [Fact]
    public async Task Each_soul_has_its_own_chain()
    {
        SkipWithoutDatabase();
        var service = Service();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await service.AppendAsync(Append(first, 0), TestContext.Current.CancellationToken);
        await service.AppendAsync(Append(first, 1, charter: _charterB), TestContext.Current.CancellationToken);
        var other = await service.AppendAsync(Append(second, 0), TestContext.Current.CancellationToken);

        Assert.Equal(1, other.Version);
        Assert.Equal(2, await service.GetHeadAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(1, await service.GetHeadAsync(second, TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.GetHeadAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preferences_are_stored_canonically_so_key_order_does_not_change_the_hash()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        var a = await service.AppendAsync(new SoulVersionAppend(soulId, 0, _charterA, "{\"verbosity\":2,\"tone\":\"dry\"}", "owner:dom", "seed"), TestContext.Current.CancellationToken);
        var b = await service.AppendAsync(new SoulVersionAppend(soulId, 1, _charterA, "{ \"tone\": \"dry\", \"verbosity\": 2 }", "owner:dom", "same again"), TestContext.Current.CancellationToken);

        Assert.Equal(a.ContentSha256, b.ContentSha256);
        Assert.Equal("{\"tone\":\"dry\",\"verbosity\":2}", (await service.GetAsync(soulId, 1, TestContext.Current.CancellationToken))!.PreferencesJson);
    }

    [Fact]
    public async Task A_changed_preference_value_changes_the_content_hash()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        var a = await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);
        var b = await service.AppendAsync(Append(soulId, 1, prefs: "{\"tone\":\"warm\",\"verbosity\":2}"), TestContext.Current.CancellationToken);

        Assert.NotEqual(a.ContentSha256, b.ContentSha256);
    }

    [Fact]
    public async Task Decision_ref_round_trips_and_a_blank_one_is_stored_as_null()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        await service.AppendAsync(new SoulVersionAppend(soulId, 0, _charterA, _prefs, "owner:dom", "seed", "  decisions/0007-tone  "), TestContext.Current.CancellationToken);
        await service.AppendAsync(new SoulVersionAppend(soulId, 1, _charterA, _prefs, "owner:dom", "blank ref", "   "), TestContext.Current.CancellationToken);

        Assert.Equal("decisions/0007-tone", (await service.GetAsync(soulId, 1, TestContext.Current.CancellationToken))!.DecisionRef);
        Assert.Null((await service.GetAsync(soulId, 2, TestContext.Current.CancellationToken))!.DecisionRef);
    }

    [Fact]
    public async Task Get_returns_null_for_a_missing_version()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();
        await service.AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);

        Assert.Null(await service.GetAsync(soulId, 2, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    public async Task A_charter_hash_that_is_not_lowercase_sha256_is_refused_before_any_write(string charter)
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(Append(soulId, 0, charter: charter), TestContext.Current.CancellationToken));

        Assert.Equal(0, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_blank_reason_or_author_is_refused()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(Append(soulId, 0, reason: "  "), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(new SoulVersionAppend(soulId, 0, _charterA, _prefs, " ", "r"), TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_negative_expected_version_is_refused()
    {
        SkipWithoutDatabase();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().AppendAsync(Append(Guid.NewGuid(), -1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_preferences_are_refused_before_any_write()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(Append(soulId, 0, prefs: "{oops"), TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_runtime_role_cannot_update_or_delete_version_rows()
    {
        SkipWithoutDatabase();
        var soulId = Guid.NewGuid();
        await Service().AppendAsync(Append(soulId, 0), TestContext.Current.CancellationToken);

        await using var conn = await db.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var role = conn.CreateCommand())
        {
            role.CommandText = "SET ROLE bazaar_app";
            await role.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE soul_versions SET reason = 'rewritten' WHERE soul_id = @id";
        update.Parameters.AddWithValue("id", soulId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    private static async Task<SoulVersion?> TryAppendAsync(SoulVersioningService service, SoulVersionAppend append)
    {
        try
        {
            return await service.AppendAsync(append, TestContext.Current.CancellationToken);
        }
        catch (SoulVersionConflictException)
        {
            return null;
        }
    }

    private static async Task WriterAsync(SoulVersioningService service, Guid soulId, int writer, int appends)
    {
        for (var done = 0; done < appends;)
        {
            var head = await service.GetHeadAsync(soulId, TestContext.Current.CancellationToken);
            try
            {
                await service.AppendAsync(Append(soulId, head, reason: $"writer {writer}"), TestContext.Current.CancellationToken);
                done++;
            }
            catch (SoulVersionConflictException)
            {
                // Another writer won the race; read the head again and retry.
            }
        }
    }
}
