using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Security.Tests.Audit;

/// <summary>
/// Pins the exact export bytes. The expected strings were captured from the reflection-based serializer before the
/// source-generated context replaced it, so any change to naming, escaping or property order fails here.
/// </summary>
public class AuditExportWireFormatTests
{
    private static readonly DateTimeOffset _ts = new(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Export_lines_match_the_captured_wire_format()
    {
        var first = AuditChain.Append(null, _ts, "owner", "config.change", "{\"key\":\"audit.export\"}");
        var second = AuditChain.Append(first, _ts.AddMilliseconds(5), "agent:charles <élan> & \"q\"", "tool.call", "line1\nline2\t\u00e9\U0001F600\u0060\u0001");

        var text = AuditExport.ToJsonLines([first, second]);

        Assert.Equal(
            """
            {"seq":1,"ts":"2026-10-25T12:00:00-05:00","actor":"owner","kind":"config.change","payload":"{\u0022key\u0022:\u0022audit.export\u0022}","prevHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","hash":"qJ9tdXoAsvoA9n1xSzvujoCq5xQeyVKD4Xt/ww7fbKA="}
            {"seq":2,"ts":"2026-10-25T12:00:00.005-05:00","actor":"agent:charles \u003C\u00E9lan\u003E \u0026 \u0022q\u0022","kind":"tool.call","payload":"line1\nline2\t\u00E9\uD83D\uDE00\u0060\u0001","prevHash":"qJ9tdXoAsvoA9n1xSzvujoCq5xQeyVKD4Xt/ww7fbKA=","hash":"LhG4nqhr7qOlfod2fawUDaxg0YIpkOn2wNt3i\u002Bf5/DQ="}

            """,
            text);
    }

    [Fact]
    public void Captured_lines_import_back_to_the_original_records()
    {
        var first = AuditChain.Append(null, _ts, "owner", "config.change", "{\"key\":\"audit.export\"}");
        var second = AuditChain.Append(first, _ts.AddMilliseconds(5), "agent:charles <élan> & \"q\"", "tool.call", "line1\nline2\t\u00e9\U0001F600\u0060\u0001");

        var parsed = AuditExport.FromJsonLines(AuditExport.ToJsonLines([first, second]));

        Assert.Equal(2, parsed.Count);
        Assert.Equal(second.Actor, parsed[1].Actor);
        Assert.Equal(second.Payload, parsed[1].Payload);
        Assert.Equal(second.Ts, parsed[1].Ts);
        Assert.Equal(second.Hash, parsed[1].Hash);
        Assert.True(AuditChain.Verify(parsed, second.Hash).IsValid);
    }
}
