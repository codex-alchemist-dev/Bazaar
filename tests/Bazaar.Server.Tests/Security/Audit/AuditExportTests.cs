using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Tests.Security.Audit;

public class AuditExportTests
{
    [Fact]
    public void Export_round_trip_keeps_every_field_and_still_verifies()
    {
        var ts = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var first = AuditChain.Append(null, ts, "owner", "config.change", "{\"key\":\"audit.export\"}");
        var second = AuditChain.Append(first, ts.AddMilliseconds(5), "agent:charles", "tool.call", "{}");

        var text = AuditExport.ToJsonLines([first, second]);
        var parsed = AuditExport.FromJsonLines(text);

        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(first.Ts, parsed[0].Ts);
        Assert.Equal(first.Payload, parsed[0].Payload);
        Assert.Equal(first.Hash, parsed[0].Hash);
        Assert.Equal(second.PrevHash, parsed[1].PrevHash);
        Assert.Equal(second.Hash, parsed[1].Hash);
        Assert.True(AuditChain.Verify(parsed, second.Hash).IsValid);
    }

    [Fact]
    public void Export_of_an_empty_log_is_empty()
    {
        Assert.Equal(string.Empty, AuditExport.ToJsonLines([]));
        Assert.Empty(AuditExport.FromJsonLines(string.Empty));
    }

    [Fact]
    public void Tampered_export_line_fails_verification_after_import()
    {
        var ts = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var first = AuditChain.Append(null, ts, "owner", "approval.granted", "{\"tool\":\"github.merge\"}");
        var second = AuditChain.Append(first, ts.AddSeconds(1), "owner", "approval.denied", "{}");

        var text = AuditExport.ToJsonLines([first, second]).Replace("github.merge", "github.push", StringComparison.Ordinal);
        var parsed = AuditExport.FromJsonLines(text);

        Assert.False(AuditChain.Verify(parsed).IsValid);
    }
}
