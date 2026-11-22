using System.Text;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentCodecTests
{
    private static async Task<IncidentRecord> SampleAsync()
    {
        IncidentHarness h = new();
        h.Planner = new IncidentRecoveryPlanner(
            Bazaar.Kodo.Tests.Responder.ResponderTestKit.Catalogue(),
            new IncidentOptions { CauseRunbooks = new Dictionary<string, IReadOnlyList<string>> { ["host.disk.free"] = ["fix.disk_space"] } });
        IncidentController controller = h.Controller();
        IncidentResult opened = await controller.OpenAsync(
            IncidentHarness.AutoOpen(IncidentTrigger.CriticalProbeRed, "host.disk.free:data"), CancellationToken.None);
        return (await controller.AdvanceAsync(IncidentHarness.Advance(opened.Incident!.Id, IncidentPhase.Investigating), CancellationToken.None)).Incident!;
    }

    [Fact]
    public async Task ARecordRoundTripsAsCamelCaseJsonWithEnumWords()
    {
        IncidentRecord record = await SampleAsync();
        byte[] bytes = IncidentCodec.ToUtf8Bytes(record);
        string json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"phase\":\"Investigating\"", json, StringComparison.Ordinal);
        Assert.Contains("\"freezeApplied\":true", json, StringComparison.Ordinal);
        Assert.True(IncidentCodec.TryParseRecord(bytes, out IncidentRecord? parsed));
        Assert.Equal(record.Id, parsed!.Id);
        Assert.Equal(record.Plan, parsed.Plan);
        Assert.Equal(record.Transitions, parsed.Transitions);
        Assert.Equal(IncidentCodec.ToUtf8Bytes(record), IncidentCodec.ToUtf8Bytes(parsed));
    }

    [Fact]
    public async Task UnknownMembersAreRejected()
    {
        string json = Encoding.UTF8.GetString(IncidentCodec.ToUtf8Bytes(await SampleAsync()));
        string extra = json.Replace("\"version\":1", "\"version\":1,\"surprise\":true", StringComparison.Ordinal);

        Assert.NotEqual(json, extra);
        Assert.False(IncidentCodec.TryParseRecord(Encoding.UTF8.GetBytes(extra), out _));
    }

    [Fact]
    public async Task BadVersionBadShapeAndInconsistentHistoryAreRejected()
    {
        IncidentRecord record = await SampleAsync();
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Version = 2 }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Id = "inc-000002" }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Number = 0, Id = "inc-000000" }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Phase = IncidentPhase.Closed }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Title = "line\nbreak" }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Cause = "has space" }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Sequence = -1 }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Categories = ["Bad Id"] }), out _));

        IncidentTransition[] skipped = [record.Transitions[0], record.Transitions[1] with { From = IncidentPhase.Opened }];
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Transitions = skipped }), out _));

        IncidentTransition[] illegal = [record.Transitions[0], record.Transitions[1] with { To = IncidentPhase.Closed }];
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Transitions = illegal, Phase = IncidentPhase.Closed }), out _));
    }

    [Fact]
    public void EmptyOversizedAndMalformedInputNeverThrows()
    {
        Assert.False(IncidentCodec.TryParseRecord(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(IncidentCodec.TryParseRecord(new byte[IncidentCodec.MaxBytes + 1], out _));
        Assert.False(IncidentCodec.TryParseRecord("{not json"u8, out _));
        Assert.False(IncidentCodec.TryParseRecord("null"u8, out _));
        Assert.False(IncidentCodec.TryParseRecord("[]"u8, out _));
    }

    [Fact]
    public async Task ARecordWithMoreEntriesThanTheLimitIsRejected()
    {
        IncidentRecord record = await SampleAsync();
        PlannedRunbook[] plan = Enumerable.Range(0, IncidentRecord.MaxPlan + 1).Select(i => new PlannedRunbook("fix.x" + i, 1, "t", false)).ToArray();
        IncidentRunLink[] runs = Enumerable.Range(0, IncidentRecord.MaxRuns + 1).Select(i => new IncidentRunLink("r" + i, "fix.x", RunbookPhase.Running)).ToArray();

        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Plan = plan }), out _));
        Assert.False(IncidentCodec.TryParseRecord(IncidentCodec.ToUtf8Bytes(record with { Runs = runs }), out _));
    }
}
