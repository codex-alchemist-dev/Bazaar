using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Identity.Kek.Tests;

public sealed class KekCustodyRecorderTests
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

    private sealed class Failing : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new InvalidOperationException("no table");
    }

    [Theory]
    [InlineData("file", "file")]
    [InlineData("dpapi", "os-user")]
    [InlineData("openbao", "remote")]
    [InlineData("tpm", "hardware")]
    [InlineData("other", "unknown")]
    public void Custody_class_follows_provider_kind(string kind, string expected) =>
        Assert.Equal(expected, KekCustodyRecorder.CustodyClassFor(kind));

    [Fact]
    public async Task Start_records_one_custody_entry_for_the_configured_kind()
    {
        var log = new Capture();
        var recorder = new KekCustodyRecorder(new AuditLogKekSink(log), " TPM ", NullLogger<KekCustodyRecorder>.Instance);

        await recorder.StartAsync(CancellationToken.None);

        var e = Assert.Single(log.Entries);
        Assert.Equal(AuditLogKekSink.CustodyEventKind, e.Kind);
        Assert.Contains("\"provider\":\"tpm\"", e.Payload);
        Assert.Contains("\"custody\":\"hardware\"", e.Payload);
    }

    [Fact]
    public async Task Blank_kind_records_the_platform_default()
    {
        var log = new Capture();
        await new KekCustodyRecorder(new AuditLogKekSink(log), null, NullLogger<KekCustodyRecorder>.Instance).StartAsync(CancellationToken.None);

        Assert.Contains($"\"provider\":\"{KekProviderFactory.DefaultKind}\"", Assert.Single(log.Entries).Payload);
    }

    [Fact]
    public async Task No_audit_log_does_nothing() =>
        await new KekCustodyRecorder(null, "file", NullLogger<KekCustodyRecorder>.Instance).StartAsync(CancellationToken.None);

    [Fact]
    public async Task A_failed_write_does_not_stop_the_host() =>
        await new KekCustodyRecorder(new AuditLogKekSink(new Failing()), "file", NullLogger<KekCustodyRecorder>.Instance).StartAsync(CancellationToken.None);
}
