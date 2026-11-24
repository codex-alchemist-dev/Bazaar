using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.WorkLife;
using Npgsql;

namespace Bazaar.Server.Presence.Postgres.Tests;

/// <summary>Checks that need no database: the enum text, the statement text, the parameter order and the embedded migration.</summary>
public class PresenceColumnCodecAndSqlTests
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(PgPresenceStore).Assembly);

    [Fact]
    public void Every_availability_and_activity_kind_round_trips()
    {
        foreach (var a in Enum.GetValues<PresenceAvailability>())
        {
            Assert.Equal(a, PresenceColumnCodec.ParseAvailability(PresenceColumnCodec.Availability(a)));
        }

        foreach (var k in Enum.GetValues<PresenceActivityKind>())
        {
            Assert.Equal(k, PresenceColumnCodec.ParseActivity(PresenceColumnCodec.Activity(k)));
        }
    }

    [Fact]
    public void Overlay_flags_round_trip_in_a_fixed_order()
    {
        Assert.Equal("none", PresenceColumnCodec.Overlay(PresenceOverlay.None));
        Assert.Equal(PresenceOverlay.None, PresenceColumnCodec.ParseOverlay("none"));

        var both = PresenceOverlay.Throttled | PresenceOverlay.OnBreak;
        Assert.Equal("on_break,throttled", PresenceColumnCodec.Overlay(both));
        Assert.Equal(both, PresenceColumnCodec.ParseOverlay("on_break,throttled"));

        var all = Enum.GetValues<PresenceOverlay>().Aggregate(PresenceOverlay.None, (acc, f) => acc | f);
        Assert.Equal(all, PresenceColumnCodec.ParseOverlay(PresenceColumnCodec.Overlay(all)));
    }

    [Fact]
    public void Unknown_stored_text_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => PresenceColumnCodec.ParseAvailability("nap"));
        Assert.Throws<FormatException>(() => PresenceColumnCodec.ParseActivity("dancing"));
        Assert.Throws<FormatException>(() => PresenceColumnCodec.ParseOverlay("on_break,sleepy"));
    }

    [Fact]
    public void The_upsert_replaces_a_row_only_when_the_incoming_revision_is_greater()
    {
        Assert.Contains("ON CONFLICT (agent_id) DO UPDATE", PgPresenceStore.UpsertSql);
        Assert.Contains("WHERE agent_activity.revision < EXCLUDED.revision", PgPresenceStore.UpsertSql);
    }

    [Fact]
    public void Parameters_follow_the_statement_in_order_and_null_columns_bind_as_null()
    {
        var t = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(2));
        var row = new PresenceRow(Guid.NewGuid(), 7, PresenceAvailability.OnShift, PresenceOverlay.Throttled, "Working",
            PresenceActivityKind.Tool, null, null, null, t, t);
        using var cmd = new NpgsqlCommand(PgPresenceStore.UpsertSql);

        PgPresenceStore.AddParameters(cmd, row);

        Assert.Equal(11, cmd.Parameters.Count);
        Assert.Equal(row.AgentId, cmd.Parameters[0].Value);
        Assert.Equal(7L, cmd.Parameters[1].Value);
        Assert.Equal("on_shift", cmd.Parameters[2].Value);
        Assert.Equal("throttled", cmd.Parameters[3].Value);
        Assert.Equal("tool", cmd.Parameters[5].Value);
        Assert.Equal(DBNull.Value, cmd.Parameters[6].Value);
        Assert.Equal(DBNull.Value, cmd.Parameters[8].Value);
        Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)cmd.Parameters[9].Value!).Offset);
    }

    [Fact]
    public async Task A_label_over_eighty_characters_is_refused_before_any_database_call()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=unreachable.invalid");
        var store = new PgPresenceStore(dataSource);
        var t = DateTimeOffset.UnixEpoch;
        var row = new PresenceRow(Guid.NewGuid(), 1, PresenceAvailability.OnShift, PresenceOverlay.None, "Working",
            PresenceActivityKind.Idle, null, null, new string('x', PresenceRow.MaxLabelLength + 1), t, t);

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.UpsertAsync(row, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_migration_lives_in_the_presence_namespace_not_in_common()
    {
        Assert.Contains("presence", _source.Namespaces());
        var file = Assert.Single(_source.Load("presence"), f => f.Version == 1);
        Assert.Equal("agent_activity", file.Name);
        Assert.Contains("CREATE TABLE agent_activity (", file.Sql);
        Assert.Contains("label         varchar(80)", file.Sql);
        Assert.Empty(_source.Load(MigrationRunner.Common));
    }
}
