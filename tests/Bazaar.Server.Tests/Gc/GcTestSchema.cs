using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>
/// The doc 01 / doc 04 GC tables, as the log code needs them, with the real constraints: gap-free key, idempotency unique key,
/// 16 hash partitions, event-kind FK and triggers that refuse UPDATE and DELETE on the log. Stand-in until migration
/// <c>common/0001</c> (G1) lands; then <see cref="GcDb"/> is replaced by the migrated template.
/// </summary>
internal static class GcTestSchema
{
    public const string Ddl = """
        CREATE TABLE gc (
            gc_id uuid PRIMARY KEY,
            head_seq bigint NOT NULL DEFAULT 0,
            archived_at timestamptz NULL
        );
        CREATE TABLE gc_event_kind (kind text PRIMARY KEY);
        INSERT INTO gc_event_kind VALUES ('message'), ('message_edit'), ('system'), ('structured'), ('quarantine'), ('handover'), ('external_mail');
        CREATE TABLE gc_event (
            gc_id uuid NOT NULL REFERENCES gc,
            seq bigint NOT NULL,
            event_id uuid NOT NULL,
            kind text NOT NULL REFERENCES gc_event_kind,
            author_kind text NOT NULL,
            author_id uuid NOT NULL,
            session_id uuid NULL,
            source_trust text NOT NULL,
            tainted_at_write boolean NOT NULL,
            taint_sources jsonb NULL,
            message_id uuid NULL,
            version int NULL,
            target_seq bigint NULL,
            reply_to_seq bigint NULL,
            payload jsonb NULL,
            payload_ct bytea NULL,
            key_id uuid NULL,
            client_msg_id text NOT NULL,
            created_at timestamptz NOT NULL,
            PRIMARY KEY (gc_id, seq),
            UNIQUE (gc_id, author_id, client_msg_id)
        ) PARTITION BY HASH (gc_id);
        CREATE TABLE gc_message (
            gc_id uuid NOT NULL REFERENCES gc,
            message_id uuid NOT NULL,
            first_seq bigint NOT NULL,
            author_id uuid NOT NULL,
            PRIMARY KEY (gc_id, message_id)
        ) PARTITION BY HASH (gc_id);
        CREATE TABLE gc_message_version (
            gc_id uuid NOT NULL REFERENCES gc,
            message_id uuid NOT NULL,
            version int NOT NULL,
            body_ct bytea NOT NULL,
            key_id uuid NOT NULL,
            editor_id uuid NOT NULL,
            created_at timestamptz NOT NULL,
            PRIMARY KEY (gc_id, message_id, version)
        ) PARTITION BY HASH (gc_id);
        CREATE TABLE gc_mention (
            gc_id uuid NOT NULL REFERENCES gc,
            seq bigint NOT NULL,
            target_principal_id uuid NOT NULL,
            PRIMARY KEY (gc_id, seq, target_principal_id)
        ) PARTITION BY HASH (gc_id);
        DO $$
        DECLARE t text; i int;
        BEGIN
            FOREACH t IN ARRAY ARRAY['gc_event', 'gc_message', 'gc_message_version', 'gc_mention'] LOOP
                FOR i IN 0..15 LOOP
                    EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES WITH (MODULUS 16, REMAINDER %s)', t || '_p' || i, t, i);
                END LOOP;
            END LOOP;
        END $$;
        CREATE FUNCTION refuse_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'the GC log is append-only' USING ERRCODE = '42501'; END $$;
        CREATE TRIGGER gc_event_append_only BEFORE UPDATE OR DELETE ON gc_event FOR EACH ROW EXECUTE FUNCTION refuse_change();
        CREATE TRIGGER gc_message_version_append_only BEFORE UPDATE OR DELETE ON gc_message_version FOR EACH ROW EXECUTE FUNCTION refuse_change();
        """;
}

/// <summary>A cloned test database with the GC schema and helpers to create GCs and read raw state.</summary>
public sealed class GcDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("GcDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        // A small pool per class: the CI server allows 100 connections in total and test classes run in parallel.
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        await using var cmd = DataSource.CreateCommand(GcTestSchema.Ddl);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Guid> CreateGcAsync(bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO gc (gc_id, archived_at) VALUES ($1, CASE WHEN $2 THEN now() END)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = archived });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

internal static class GcTestSkip
{
    // Windows without BAZAAR_TEST_PG has no database by design (test.ci.windows_db_job = false); Linux never skips.
    public static void WithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
}
