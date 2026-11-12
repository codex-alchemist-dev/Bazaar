using Npgsql;

namespace Bazaar.Server.Tests.Gc.Body;

/// <summary>The partial unique index on <c>person_key</c>: one live key per person, and erased keys do not count (database).</summary>
public sealed class PersonKeyActiveUniqueTests(GcDb db) : IClassFixture<GcDb>
{
    private async Task<Guid> InsertAsync(Guid person)
    {
        var keyId = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO person_key (key_id, person_id, wrapped_key) VALUES ($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = keyId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = person });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = [1, 2, 3] });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return keyId;
    }

    [Fact]
    public async Task A_second_live_key_for_one_person_is_refused()
    {
        GcTestSkip.WithoutDatabase();
        var person = Guid.CreateVersion7();
        await InsertAsync(person);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(person));

        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task A_new_live_key_is_allowed_once_the_old_one_is_erased()
    {
        GcTestSkip.WithoutDatabase();
        var person = Guid.CreateVersion7();
        var old = await InsertAsync(person);

        await using (var erase = db.DataSource.CreateCommand("UPDATE person_key SET wrapped_key = NULL, destroyed_at = now() WHERE key_id = $1"))
        {
            erase.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = old });
            await erase.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var fresh = await InsertAsync(person);

        Assert.NotEqual(old, fresh);
    }
}
