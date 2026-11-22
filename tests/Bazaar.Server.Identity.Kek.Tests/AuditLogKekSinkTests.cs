using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Security.Audit;
using Xunit;

namespace Bazaar.Server.Identity.Kek.Tests;

public sealed class AuditLogKekSinkTests
{
    private sealed class Capture : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Record_forwards_kind_payload_and_actor()
    {
        var log = new Capture();
        await new AuditLogKekSink(log, "kek-rewrap").RecordAsync("identity.kek.rewrap.started", "{\"rows\":3}", CancellationToken.None);

        var e = Assert.Single(log.Entries);
        Assert.Equal("kek-rewrap", e.Actor);
        Assert.Equal("identity.kek.rewrap.started", e.Kind);
        Assert.Equal("{\"rows\":3}", e.Payload);
    }

    [Fact]
    public async Task Custody_event_names_provider_and_class_only()
    {
        var log = new Capture();
        await new AuditLogKekSink(log).RecordCustodyAsync("tpm", "virtual", CancellationToken.None);

        var e = Assert.Single(log.Entries);
        Assert.Equal(AuditLogKekSink.CustodyEventKind, e.Kind);
        Assert.Equal("kek", e.Actor);
        Assert.Contains("\"provider\":\"tpm\"", e.Payload);
        Assert.Contains("\"custody\":\"virtual\"", e.Payload);
    }

    [Fact]
    public async Task Custody_event_refuses_blank_values()
    {
        var sink = new AuditLogKekSink(new Capture());
        await Assert.ThrowsAsync<ArgumentException>(() => sink.RecordCustodyAsync(" ", "hardware", CancellationToken.None));
    }
}
