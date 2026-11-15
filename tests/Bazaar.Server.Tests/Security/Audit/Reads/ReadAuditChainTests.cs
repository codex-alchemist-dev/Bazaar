using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Security.Audit.Reads;
using Bazaar.Server.Tests.Security.Audit.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Audit.Reads;

/// <summary>Read rows written through <see cref="ReadAuditor"/> land on the real hash chain (doc 48 section 5.2).</summary>
public sealed class ReadAuditChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Read_rows_append_to_the_chain_and_the_chain_verifies()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);
        var auditor = new ReadAuditor(log, TimeProvider.System);

        await using (auditor.BeginScope(new ReadAccessContext("human:owner", "tool.gc.read", Session: "s1")))
        {
            await auditor.RecordAsync(DataRead.FromSql("gc_message", "select id from gc_message where seq > $1", 4), Ct);
            await auditor.RecordAsync(DataRead.FromSql("gc_message", "select id from gc_message where seq > $1", 6), Ct);
        }

        await auditor.RecordAsync(new DataRead("person_key", 1, "select", new string('e', 64), ObjectId: "person-9"), Ct);

        var records = new List<AuditRecord>();
        await foreach (var record in log.StreamAsync(Ct))
            records.Add(record);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("data.read", r.Kind));
        Assert.True((await log.VerifyAsync(ct: Ct)).IsValid);
    }
}
