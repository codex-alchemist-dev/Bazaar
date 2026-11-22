using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Responder;
using Bazaar.Kodo.Tests.Responder;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Incidents;

/// <summary>Keeps incidents as JSON bytes, so every save and load goes through the codec like a real store would.</summary>
internal sealed class MemoryIncidentStore : IIncidentStore
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public long Counter { get; set; }

    public bool FailSaves { get; set; }

    public ValueTask<long> NextNumberAsync(CancellationToken cancellationToken) => ValueTask.FromResult(++Counter);

    public ValueTask<IncidentRecord?> LoadAsync(string incidentId, CancellationToken cancellationToken)
    {
        if (!Files.TryGetValue(incidentId, out byte[]? bytes))
        {
            return ValueTask.FromResult<IncidentRecord?>(null);
        }

        if (!IncidentCodec.TryParseRecord(bytes, out IncidentRecord? record))
        {
            throw new IOException("state unreadable");
        }

        return ValueTask.FromResult(record);
    }

    public ValueTask SaveAsync(IncidentRecord record, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            throw new IOException("disk full");
        }

        if (Files.TryGetValue(record.Id, out byte[]? existing)
            && IncidentCodec.TryParseRecord(existing, out IncidentRecord? old) && old!.Sequence >= record.Sequence)
        {
            throw new IOException("stale");
        }

        Files[record.Id] = IncidentCodec.ToUtf8Bytes(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<IncidentRecord>> ListUnsettledAsync(CancellationToken cancellationToken)
    {
        List<IncidentRecord> list = [];
        foreach (byte[] bytes in Files.Values)
        {
            Assert.True(IncidentCodec.TryParseRecord(bytes, out IncidentRecord? record));
            if (record!.IsActive || !record.FreezeReleased)
            {
                list.Add(record);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<IncidentRecord>>(list.OrderBy(r => r.Number).ToList());
    }
}

internal sealed class MemoryIncidentJournal : IIncidentJournal
{
    public List<IncidentJournalEntry> Entries { get; } = [];

    public bool Fail { get; set; }

    public ValueTask AppendAsync(IncidentJournalEntry entry, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new IOException("journal down");
        }

        Entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeFreezeSink : IIncidentFreezeSink
{
    public int Applied { get; private set; }

    public int Released { get; private set; }

    public bool FailApply { get; set; }

    public bool FailRelease { get; set; }

    public ValueTask ApplyAsync(string incidentId, CancellationToken cancellationToken)
    {
        if (FailApply)
        {
            throw new IOException("sink down");
        }

        Applied++;
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(string incidentId, CancellationToken cancellationToken)
    {
        if (FailRelease)
        {
            throw new IOException("sink down");
        }

        Released++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingChannel(AlertChannelKind kind) : IAlertChannel
{
    public AlertChannelKind Kind { get; } = kind;

    public List<AlertDelivery> Sent { get; } = [];

    public ValueTask<AlertSendOutcome> SendAsync(AlertDelivery delivery, CancellationToken cancellationToken)
    {
        Sent.Add(delivery);
        return ValueTask.FromResult(AlertSendOutcome.Sent);
    }
}

internal sealed class IncidentHarness
{
    public IncidentHarness(IncidentOptions? options = null, bool withNotifier = false)
    {
        Options = options ?? new IncidentOptions();
        Clock = ResponderTestKit.Clock();
        Router = new AlertRouter(new AlertOptions(), Clock);
        Channels = Enum.GetValues<AlertChannelKind>().Select(kind => new RecordingChannel(kind)).ToList();
        Dispatcher = new AlertDispatcher(Router, Channels);
        Notifier = withNotifier ? new IncidentNotifier(Router, Dispatcher, Clock) : null;
    }

    public IncidentOptions Options { get; }

    public FakeTimeProvider Clock { get; }

    public MemoryIncidentStore Store { get; } = new();

    public MemoryIncidentJournal Journal { get; } = new();

    public FakeFreezeSink Freeze { get; } = new();

    public ScriptedGate Gate { get; } = new();

    public AlertRouter Router { get; }

    public AlertDispatcher Dispatcher { get; }

    public List<RecordingChannel> Channels { get; }

    public IncidentNotifier? Notifier { get; }

    public IncidentRecoveryPlanner? Planner { get; set; }

    public IncidentEvidenceCollector? Evidence { get; set; }

    public IncidentController Controller() =>
        new(Store, Journal, Freeze, Gate, Options, Clock, Planner, Evidence, Notifier);

    public void ApproveAll() => Gate.Decide = _ => ApprovalDecision.Approved;

    public static IReadOnlySet<string> Controls(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    public static IncidentOpenRequest ManualOpen(IReadOnlySet<string>? controls = null, string cause = "host.disk.free:data") =>
        new(
            IncidentTrigger.Manual, cause, "Data disk full", AlertSeverity.Critical, "owner", "owner_declared",
            ["host"], ["host.disk.free:data"], controls ?? Controls(KodoControls.IncidentEnter));

    public static IncidentOpenRequest AutoOpen(IncidentTrigger trigger = IncidentTrigger.IntegrityFailure, string cause = "kodo.self.signature_verify") =>
        new(trigger, cause, "Integrity failure", AlertSeverity.Warning, "ignored", "auto_trigger", ["kodo"], [], Controls());

    public static IncidentAdvanceRequest Advance(string id, IncidentPhase to, IReadOnlySet<string>? controls = null, DateTimeOffset? green = null) =>
        new(id, to, "owner", "step", controls ?? Controls(), green);

    /// <summary>Opens an automatic incident and walks it to the phase asked for. Exit is approved when the gate approves.</summary>
    public async Task<IncidentRecord> WalkAsync(IncidentController controller, IncidentPhase target)
    {
        IncidentResult opened = await controller.OpenAsync(AutoOpen(), CancellationToken.None);
        Assert.True(opened.Ok, opened.Code);
        IncidentRecord record = opened.Incident!;
        foreach (IncidentPhase step in new[] { IncidentPhase.Investigating, IncidentPhase.Recovering, IncidentPhase.Resolved, IncidentPhase.Closed })
        {
            if (record.Phase >= target)
            {
                break;
            }

            IncidentResult moved = await controller.AdvanceAsync(
                Advance(record.Id, step, Controls(KodoControls.IncidentExit), Clock.GetUtcNow() - TimeSpan.FromHours(1)), CancellationToken.None);
            Assert.True(moved.Ok, moved.Code);
            record = moved.Incident!;
        }

        return record;
    }
}
