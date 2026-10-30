using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Constellation;

/// <summary>A cloned test database migrated with every embedded migration, so the Constellation views (0019) exist.</summary>
public sealed class ConstellationDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("ConstellationDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }
}

/// <summary>
/// One set of fixture rows for a test. Every name carries a fresh tag, so tests that share a database never collide.
/// <see cref="Owner"/> is the owner principal (tests pass is_owner = true for it); <see cref="AgentA"/> and
/// <see cref="AgentB"/> are agents; <see cref="Human"/> is a GC member; <see cref="Outsider"/> is in no GC.
/// </summary>
public sealed class ConstellationSeed
{
    public string Tag { get; } = Guid.CreateVersion7().ToString("N");
    public Guid VaultId { get; } = Guid.CreateVersion7();
    public Guid Owner { get; } = Guid.CreateVersion7();
    public Guid AgentA { get; } = Guid.CreateVersion7();
    public Guid AgentB { get; } = Guid.CreateVersion7();
    public Guid Human { get; } = Guid.CreateVersion7();
    public Guid Outsider { get; } = Guid.CreateVersion7();

    public Guid PublicDoc { get; } = Guid.CreateVersion7();
    public Guid PlanDoc { get; } = Guid.CreateVersion7();
    public Guid MemoryDoc { get; } = Guid.CreateVersion7();

    public Guid GeneralGc { get; } = Guid.CreateVersion7();
    public Guid DmGc { get; } = Guid.CreateVersion7();
    public Guid ThinkingGc { get; } = Guid.CreateVersion7();

    public Guid BlockerTask { get; } = Guid.CreateVersion7();
    public Guid BlockedTask { get; } = Guid.CreateVersion7();

    /// <summary>Target text of a link from <see cref="PublicDoc"/> that resolves to nothing.</summary>
    public string MissingTarget => "Missing " + Tag;

    /// <summary>Target text of a link from <see cref="MemoryDoc"/> that resolves to nothing.</summary>
    public string PrivateTarget => "Private " + Tag;

    /// <summary>The projection's node id for an unresolved link target (migration 0019).</summary>
    public static string GhostId(string targetRaw) => "ghost:" + targetRaw;

    public static async Task<ConstellationSeed> InsertAsync(NpgsqlDataSource ds)
    {
        var s = new ConstellationSeed();

        // Vault: a public note with a resolved link to a plan and an unresolved link to a missing note; one agent's private
        // memory note with its own unresolved link.
        await ExecAsync(ds,
            "INSERT INTO vault_docs (doc_id, vault_id, path, title, type, area, owner) VALUES ($1, $2, $3, $4, 'deep-dive', 'company', NULL)",
            s.PublicDoc, s.VaultId, $"notes/public-{s.Tag}.md", $"Public {s.Tag}");
        await ExecAsync(ds,
            "INSERT INTO vault_docs (doc_id, vault_id, path, title, type, area, owner) VALUES ($1, $2, $3, $4, 'plan', 'company', NULL)",
            s.PlanDoc, s.VaultId, $"plans/plan-{s.Tag}.md", $"Plan {s.Tag}");
        await ExecAsync(ds,
            "INSERT INTO vault_docs (doc_id, vault_id, path, title, type, area, owner) VALUES ($1, $2, $3, $4, 'memory', 'agents/a', $5)",
            s.MemoryDoc, s.VaultId, $"agents/a/memory-{s.Tag}.md", $"Memory {s.Tag}", s.AgentA.ToString());

        await ExecAsync(ds,
            "INSERT INTO vault_blocks (doc_id, ordinal, start_line, end_line, text_hash, body) VALUES ($1, 0, 1, 1, $2, 'body')",
            s.PublicDoc, new byte[32]);
        await ExecAsync(ds,
            "INSERT INTO vault_blocks (doc_id, ordinal, start_line, end_line, text_hash, body) VALUES ($1, 0, 1, 1, $2, 'body')",
            s.MemoryDoc, new byte[32]);

        await ExecAsync(ds,
            "INSERT INTO vault_links (src_doc, src_ordinal, link_ord, kind, target_raw, target_doc) VALUES ($1, 0, 0, 'link', $2, $3)",
            s.PublicDoc, $"Plan {s.Tag}", s.PlanDoc);
        await ExecAsync(ds,
            "INSERT INTO vault_links (src_doc, src_ordinal, link_ord, kind, target_raw, target_doc) VALUES ($1, 0, 1, 'link', $2, NULL)",
            s.PublicDoc, s.MissingTarget);
        await ExecAsync(ds,
            "INSERT INTO vault_links (src_doc, src_ordinal, link_ord, kind, target_raw, target_doc) VALUES ($1, 0, 0, 'link', $2, NULL)",
            s.MemoryDoc, s.PrivateTarget);

        // GCs: a general GC, a two-member DM (members Human and AgentA; the owner is not a member), and a Thinking GC nested
        // under the general GC.
        await ExecAsync(ds, "INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', $2, $3)",
            s.GeneralGc, $"General {s.Tag}", s.Owner);
        await ExecAsync(ds, "INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'dm', $2, $3)",
            s.DmGc, $"DM {s.Tag}", s.Human);
        await ExecAsync(ds, "INSERT INTO gc (gc_id, parent_gc_id, type_key, title, created_by) VALUES ($1, $2, 'thinking', $3, $4)",
            s.ThinkingGc, s.GeneralGc, $"Thinking {s.Tag}", s.AgentA);

        await ExecAsync(ds, "INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ($1, $2, 0)", s.GeneralGc, s.Human);
        await ExecAsync(ds, "INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ($1, $2, 0)", s.GeneralGc, s.AgentA);
        await ExecAsync(ds, "INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ($1, $2, 0)", s.DmGc, s.Human);
        await ExecAsync(ds, "INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ($1, $2, 0)", s.DmGc, s.AgentA);
        await ExecAsync(ds, "INSERT INTO gc_member (gc_id, principal_id, joined_seq) VALUES ($1, $2, 0)", s.ThinkingGc, s.AgentA);

        // Kanban: a blocker task blocks a blocked task; both originate in the general GC.
        await ExecAsync(ds, "INSERT INTO tasks (id, project_id, title, state, origin_gc_id) VALUES ($1, $2, $3, 'ready', $4)",
            s.BlockerTask, Guid.CreateVersion7(), $"Blocker {s.Tag}", s.GeneralGc);
        await ExecAsync(ds, "INSERT INTO tasks (id, project_id, title, state, origin_gc_id) VALUES ($1, $2, $3, 'backlog', $4)",
            s.BlockedTask, Guid.CreateVersion7(), $"Blocked {s.Tag}", s.GeneralGc);
        await ExecAsync(ds, "INSERT INTO task_edges (blocker_id, blocked_id, created_by) VALUES ($1, $2, $3)",
            s.BlockerTask, s.BlockedTask, s.Owner);

        return s;
    }

    private static async Task ExecAsync(NpgsqlDataSource ds, string sql, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
