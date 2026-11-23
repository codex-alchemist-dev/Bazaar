using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Tests.Responder;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Incidents;

internal sealed class FakeSource(EvidenceKind kind, params (string Name, string Text)[] parts) : IEvidenceSource
{
    public EvidenceKind Kind { get; } = kind;

    public Exception? Throw { get; set; }

    public bool Hang { get; set; }

    public byte[]? Raw { get; set; }

    public EvidenceRequest? Seen { get; private set; }

    public async ValueTask<IReadOnlyList<EvidencePart>> CollectAsync(EvidenceRequest request, CancellationToken cancellationToken)
    {
        Seen = request;
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        return Raw is not null
            ? [new EvidencePart(parts[0].Name, Raw)]
            : parts.Select(part => new EvidencePart(part.Name, Encoding.UTF8.GetBytes(part.Text))).ToList();
    }
}

internal sealed class UpperRedactor : IEvidenceRedactor
{
    public Func<string, bool> Drop { get; set; } = _ => false;

    public bool Throws { get; set; }

    public byte[]? Redact(EvidenceKind kind, string name, ReadOnlySpan<byte> content)
    {
        if (Throws)
        {
            throw new InvalidOperationException("redactor failed");
        }

        return Drop(name) ? null : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("hunter2", "[removed]", StringComparison.Ordinal));
    }
}

internal sealed class MemorySink : IEvidenceSink
{
    public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

    public EvidenceManifest? Manifest { get; private set; }

    public bool FailItems { get; set; }

    public bool FailManifest { get; set; }

    public ValueTask WriteItemAsync(string incidentId, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (FailItems)
        {
            throw new IOException("store down");
        }

        Items[name] = content.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteManifestAsync(EvidenceManifest manifest, CancellationToken cancellationToken)
    {
        if (FailManifest)
        {
            throw new IOException("store down");
        }

        Manifest = manifest;
        return ValueTask.CompletedTask;
    }
}

public sealed class IncidentEvidenceTests
{
    private static readonly DateTimeOffset _opened = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static (IncidentEvidenceCollector Collector, MemorySink Sink, UpperRedactor Redactor, FakeTimeProvider Clock) Make(
        IEnumerable<IEvidenceSource> sources, EvidenceOptions? options = null)
    {
        FakeTimeProvider clock = ResponderTestKit.Clock();
        MemorySink sink = new();
        UpperRedactor redactor = new();
        return (new IncidentEvidenceCollector(sources, redactor, sink, options ?? new EvidenceOptions(), clock), sink, redactor, clock);
    }

    private static FakeSource[] AllKinds() =>
    [
        new(EvidenceKind.FleetList, ("fleet.list", "node-a")),
        new(EvidenceKind.Logs, ("logs.current", "log one"), ("logs.previous", "log two")),
        new(EvidenceKind.JournalSegment, ("journal.segment", "segment")),
        new(EvidenceKind.Configuration, ("config.sanitised", "password=hunter2")),
    ];

    [Fact]
    public async Task TheManifestIsOrderedByKindThenPartAndHashesEachItem()
    {
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make(AllKinds());
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal(["journal.segment", "logs.current", "logs.previous", "config.sanitised", "fleet.list"], manifest.Items.Select(i => i.Name));
        Assert.Equal([0, 1, 2, 3, 4], manifest.Items.Select(i => i.Index));
        Assert.All(manifest.Items, item => Assert.Equal(EvidenceStatus.Collected, item.Status));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("segment"u8)), manifest.Items[0].Sha256);
        Assert.Equal(7, manifest.Items[0].Size);
        Assert.Same(manifest, sink.Manifest);
        Assert.Null(EvidenceManifest.FirstBroken(manifest));
        Assert.Empty(EvidenceManifest.ContentMismatches(manifest, name => sink.Items.GetValueOrDefault(name)));
    }

    [Fact]
    public async Task TheRedactionHookRunsBeforeHashingAndStoring()
    {
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make(AllKinds());
        await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal("password=[removed]", Encoding.UTF8.GetString(sink.Items["config.sanitised"]));
    }

    [Fact]
    public async Task ADroppedOrFailingRedactionRefusesTheItem()
    {
        (IncidentEvidenceCollector collector, MemorySink sink, UpperRedactor redactor, _) = Make(AllKinds());
        redactor.Drop = name => name == "logs.current";
        EvidenceManifest dropped = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);
        EvidenceItem item = dropped.Items.Single(i => i.Name == "logs.current");
        Assert.Equal((EvidenceStatus.Refused, "redactor_dropped", string.Empty), (item.Status, item.Reason, item.Sha256));
        Assert.DoesNotContain("logs.current", sink.Items.Keys);

        redactor.Drop = _ => false;
        redactor.Throws = true;
        EvidenceManifest failed = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);
        Assert.All(failed.Items, i => Assert.Equal("redactor_error", i.Reason));
    }

    [Fact]
    public async Task APrivateKeyBlockIsRefusedEvenAfterRedaction()
    {
        FakeSource source = new(EvidenceKind.Configuration, ("config.sanitised", string.Empty))
        {
            Raw = Encoding.ASCII.GetBytes("a=b\n-----BEGIN RSA PRIVATE KEY-----\nabc"),
        };
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make([source]);
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal("secret_detected", Assert.Single(manifest.Items).Reason);
        Assert.Empty(sink.Items);

        FakeSource cert = new(EvidenceKind.Configuration, ("config.sanitised", string.Empty)) { Raw = "-----BEGIN CERTIFICATE-----\nabc"u8.ToArray() };
        (IncidentEvidenceCollector certCollector, _, _, _) = Make([cert]);
        Assert.Equal(EvidenceStatus.Collected, Assert.Single((await certCollector.CollectAsync("inc-000001", _opened, CancellationToken.None)).Items).Status);
    }

    [Fact]
    public async Task AnItemOverTheCapIsCutAndFlagged()
    {
        FakeSource big = new(EvidenceKind.Logs, ("logs.current", string.Empty)) { Raw = new byte[100] };
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make([big], new EvidenceOptions { MaxItemBytes = 40 });
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        EvidenceItem item = Assert.Single(manifest.Items);
        Assert.True(item.Truncated);
        Assert.Equal(40, item.Size);
        Assert.Equal(40, sink.Items["logs.current"].Length);
        Assert.Empty(EvidenceManifest.ContentMismatches(manifest, name => sink.Items.GetValueOrDefault(name)));
    }

    [Fact]
    public async Task TheTotalCapSkipsLaterItemsInsteadOfStoringThem()
    {
        FakeSource a = new(EvidenceKind.JournalSegment, ("journal.segment", string.Empty)) { Raw = new byte[30] };
        FakeSource b = new(EvidenceKind.Logs, ("logs.current", string.Empty)) { Raw = new byte[30] };
        FakeSource c = new(EvidenceKind.Configuration, ("config.sanitised", "tiny"));
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make([a, b, c], new EvidenceOptions { MaxItemBytes = 30, MaxTotalBytes = 50 });
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal([EvidenceStatus.Collected, EvidenceStatus.Skipped, EvidenceStatus.Collected], manifest.Items.Select(i => i.Status));
        Assert.Equal("total_cap", manifest.Items[1].Reason);
        Assert.DoesNotContain("logs.current", sink.Items.Keys);
    }

    [Fact]
    public async Task TheItemCountIsCappedAndTheOmissionIsCounted()
    {
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make(AllKinds(), new EvidenceOptions { MaxItems = 3 });
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal(3, manifest.Items.Count);
        Assert.Equal(2, manifest.OmittedItems);
        Assert.Equal(3, sink.Items.Count);
        Assert.Null(EvidenceManifest.FirstBroken(manifest));

        // The omitted count is part of the root, so hiding the omission is detected.
        Assert.Equal(-1, EvidenceManifest.FirstBroken(manifest with { OmittedItems = 0 }));
    }

    [Fact]
    public async Task ADuplicateNameAnInvalidNameAndAFailingSourceAreRecordedNotFatal()
    {
        FakeSource dup = new(EvidenceKind.Logs, ("logs.current", "x"), ("logs.current", "y"), ("Bad Name", "z"));
        FakeSource broken = new(EvidenceKind.Configuration, ("config.sanitised", "x")) { Throw = new InvalidOperationException("secret detail") };
        FakeSource fine = new(EvidenceKind.FleetList, ("fleet.list", "node"));
        (IncidentEvidenceCollector collector, _, _, _) = Make([dup, broken, fine]);
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal(
            [EvidenceStatus.Collected, EvidenceStatus.Refused, EvidenceStatus.Refused, EvidenceStatus.Failed, EvidenceStatus.Collected],
            manifest.Items.Select(i => i.Status));
        Assert.Equal(["ok", "duplicate_name", "invalid_part", "source_error", "ok"], manifest.Items.Select(i => i.Reason));
        Assert.DoesNotContain("secret detail", string.Join(' ', manifest.Items.Select(i => i.Reason)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreFailureMarksTheItemFailed()
    {
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make(AllKinds());
        sink.FailItems = true;
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.All(manifest.Items, item => Assert.Equal("store_error", item.Reason));
        sink.FailManifest = true;
        await Assert.ThrowsAsync<IOException>(async () => await collector.CollectAsync("inc-000001", _opened, CancellationToken.None));
    }

    [Fact]
    public async Task ASourceThatHangsTimesOutWithoutStoppingTheRest()
    {
        FakeSource hang = new(EvidenceKind.JournalSegment, ("journal.segment", "x")) { Hang = true };
        FakeSource fine = new(EvidenceKind.FleetList, ("fleet.list", "node"));
        (IncidentEvidenceCollector collector, _, _, FakeTimeProvider clock) = Make([hang, fine], new EvidenceOptions { SourceTimeout = TimeSpan.FromSeconds(5) });

        Task<EvidenceManifest> task = collector.CollectAsync("inc-000001", _opened, CancellationToken.None).AsTask();
        for (int i = 0; i < 500 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        EvidenceManifest manifest = await task;
        Assert.Equal("timeout", manifest.Items[0].Reason);
        Assert.Equal(EvidenceStatus.Collected, manifest.Items[1].Status);
    }

    [Fact]
    public async Task TheSourcesAreAskedForTheTriggerWindow()
    {
        FakeSource source = new(EvidenceKind.Logs, ("logs.current", "x"));
        (IncidentEvidenceCollector collector, _, _, FakeTimeProvider clock) = Make([source], new EvidenceOptions { TriggerWindow = TimeSpan.FromMinutes(30) });
        await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        Assert.Equal(_opened - TimeSpan.FromMinutes(30), source.Seen!.TriggerWindowStart);
        Assert.Equal(clock.GetUtcNow(), source.Seen.CollectedAt);
    }

    [Fact]
    public async Task ATamperedManifestIsDetectedWherever()
    {
        (IncidentEvidenceCollector collector, _, _, _) = Make(AllKinds());
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        EvidenceItem[] items = [.. manifest.Items];
        items[2] = items[2] with { Sha256 = new string('0', 64) };
        Assert.Equal(2, EvidenceManifest.FirstBroken(manifest with { Items = items }));

        Assert.Equal(1, EvidenceManifest.FirstBroken(manifest with { Items = [manifest.Items[0], .. manifest.Items.Skip(2)] }));
        Assert.Equal(0, EvidenceManifest.FirstBroken(manifest with { Items = [manifest.Items[1], manifest.Items[0], .. manifest.Items.Skip(2)] }));
        Assert.Equal(-1, EvidenceManifest.FirstBroken(manifest with { Items = [.. manifest.Items.Take(4)] }));
        Assert.Equal(0, EvidenceManifest.FirstBroken(manifest with { IncidentId = "inc-000002" }));
        Assert.Equal(0, EvidenceManifest.FirstBroken(manifest with { CreatedAt = manifest.CreatedAt.AddSeconds(1) }));
        Assert.Equal(-1, EvidenceManifest.FirstBroken(manifest with { Root = new string('a', 64) }));
    }

    [Fact]
    public async Task AlteredStoredContentIsDetected()
    {
        (IncidentEvidenceCollector collector, MemorySink sink, _, _) = Make(AllKinds());
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);

        sink.Items["logs.current"][0] ^= 1;
        sink.Items.Remove("fleet.list");
        sink.Items["logs.previous"] = [.. sink.Items["logs.previous"], 0];

        Assert.Equal(
            ["logs.current", "logs.previous", "fleet.list"],
            EvidenceManifest.ContentMismatches(manifest, name => sink.Items.GetValueOrDefault(name)));
    }

    [Fact]
    public async Task TheManifestSurvivesJsonAndATamperedFileIsRefused()
    {
        (IncidentEvidenceCollector collector, _, _, _) = Make(AllKinds());
        EvidenceManifest manifest = await collector.CollectAsync("inc-000001", _opened, CancellationToken.None);
        byte[] bytes = IncidentCodec.ToUtf8Bytes(manifest);

        Assert.True(IncidentCodec.TryParseManifest(bytes, out EvidenceManifest? parsed));
        Assert.Equal(manifest.Root, parsed!.Root);

        string text = Encoding.UTF8.GetString(bytes).Replace("\"size\":7", "\"size\":8", StringComparison.Ordinal);
        Assert.False(IncidentCodec.TryParseManifest(Encoding.UTF8.GetBytes(text), out _));
        Assert.False(IncidentCodec.TryParseManifest("{\"version\":1,\"extra\":1}"u8, out _));
        Assert.False(IncidentCodec.TryParseManifest(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void OptionsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceOptions { MaxItems = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceOptions { MaxItemBytes = 100, MaxTotalBytes = 50 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceOptions { SourceTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentNullException>(() => new IncidentEvidenceCollector([], null!, new MemorySink(), new EvidenceOptions(), TimeProvider.System));
    }
}
