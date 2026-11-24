using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Prediction;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Tests.Export;
using Bazaar.Kodo.Tests.Incidents;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Fuzz;

/// <summary>
/// Seeded fuzzing of the JSON decoders (doc 46 section 17.4). A decoder may reject input only with its documented exception (or a
/// false result), must stay inside the time bound, and a rejected input must leave no trace: the input bytes are unchanged and a
/// decode that fails produces nothing. A decode that succeeds must re-encode to something that decodes to the same value.
/// </summary>
public sealed class WireDecoderFuzzTests
{
    private static readonly DateTimeOffset _predictionStart = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private static KodoStatusSnapshotDto SampleSnapshot() =>
        KodoExportTestData.Snapshot(
            KodoState.Failing,
            KodoState.Failing,
            score: 40,
            KodoExportTestData.Component("host.disk.free", ProbeStatus.Red, "disk_low"),
            KodoExportTestData.Component("db.ping", ProbeStatus.Green));

    private static KodoStreamEventDto SampleStateChange() =>
        new(7, KodoStreamEventDto.StateChangedType, KodoExportTestData.Now, new KodoStateChangeDto("healthy", "frozen", "failing"), null);

    private static KodoFleetSnapshotDto SampleFleet() =>
        new(
            KodoFleetSnapshotDto.CurrentSchemaVersion,
            3,
            KodoExportTestData.Now,
            [new KodoServerStatusDto("srv-1", "Primary", "healthy", null, 100, KodoExportTestData.Now, "0.1.0", 0, false)]);

    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    public void SnapshotDecoderRejectsOnlyWithJsonException(int seed)
    {
        byte[] valid = KodoWireCodec.EncodeSnapshot(SampleSnapshot());

        FuzzKit.Run(
            "KodoWireCodec.DecodeSnapshot",
            seed,
            800,
            (random, i) => FuzzKit.Mixed(random, i, valid, FuzzKit.WireNames),
            input =>
            {
                byte[] before = [.. input];
                KodoStatusSnapshotDto? decoded = KodoWireCodec.DecodeSnapshot(input);
                Assert.Equal(before, input);
                if (decoded is not null)
                {
                    byte[] again = KodoWireCodec.EncodeSnapshot(decoded);
                    Assert.Equal(again, KodoWireCodec.EncodeSnapshot(KodoWireCodec.DecodeSnapshot(again)!));
                }
            },
            typeof(JsonException));
    }

    [Theory]
    [InlineData(111)]
    [InlineData(112)]
    [InlineData(113)]
    public void EventDecoderRejectsOnlyWithJsonException(int seed)
    {
        byte[][] valid = [KodoWireCodec.EncodeEvent(SampleStateChange()), KodoWireCodec.EncodeEvent(new KodoStreamEventDto(9, KodoStreamEventDto.SnapshotType, KodoExportTestData.Now, null, SampleSnapshot()))];

        FuzzKit.Run(
            "KodoWireCodec.DecodeEvent",
            seed,
            800,
            (random, i) => FuzzKit.Mixed(random, i, valid[i % valid.Length], FuzzKit.WireNames),
            input =>
            {
                KodoStreamEventDto? decoded = KodoWireCodec.DecodeEvent(input);
                if (decoded is not null)
                {
                    byte[] again = KodoWireCodec.EncodeEvent(decoded);
                    Assert.Equal(again, KodoWireCodec.EncodeEvent(KodoWireCodec.DecodeEvent(again)!));
                }
            },
            typeof(JsonException));
    }

    [Theory]
    [InlineData(121)]
    [InlineData(122)]
    public void FleetDecoderRejectsOnlyWithJsonException(int seed)
    {
        byte[] valid = KodoWireCodec.EncodeFleet(SampleFleet());

        FuzzKit.Run(
            "KodoWireCodec.DecodeFleet",
            seed,
            800,
            (random, i) => FuzzKit.Mixed(random, i, valid, FuzzKit.WireNames),
            input => KodoWireCodec.DecodeFleet(input),
            typeof(JsonException));
    }

    [Fact]
    public void DeeplyNestedInputIsRejectedWithoutExhaustingTheStack()
    {
        // 20,000 levels is far past the reader's depth limit (64), so this proves the limit holds for every wire entry point.
        string nested = new string('[', 20_000) + new string(']', 20_000);
        string nestedObjects = string.Concat(Enumerable.Repeat("{\"snapshot\":", 20_000)) + "null" + new string('}', 20_000);

        foreach (string text in new[] { nested, nestedObjects })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Assert.Throws<JsonException>(() => KodoWireCodec.DecodeSnapshot(bytes));
            Assert.Throws<JsonException>(() => KodoWireCodec.DecodeEvent(bytes));
            Assert.Throws<JsonException>(() => KodoWireCodec.DecodeFleet(bytes));
        }
    }

    [Fact]
    public void SnapshotRoundTripsForRandomComponentSets()
    {
        Random random = FuzzKit.Rng(131);
        ProbeStatus[] statuses = Enum.GetValues<ProbeStatus>();
        for (int round = 0; round < 60; round++)
        {
            List<KodoComponentInput> components = [];
            int count = random.Next(0, 12);
            for (int i = 0; i < count; i++)
            {
                ProbeStatus status = statuses[random.Next(statuses.Length)];
                components.Add(new KodoComponentInput(
                    $"probe_{round}.item{i}",
                    status,
                    status == ProbeStatus.Green ? null : "cause_" + i,
                    KodoExportTestData.Now.AddSeconds(-random.Next(0, 100000))));
            }

            KodoStatusSnapshotDto snapshot = KodoExportTestData.Snapshot(KodoState.Healthy, null, 100, [.. components]);
            byte[] bytes = KodoWireCodec.EncodeSnapshot(snapshot);

            Assert.Equal(bytes, KodoWireCodec.EncodeSnapshot(KodoWireCodec.DecodeSnapshot(bytes)!));
        }
    }

    [Theory]
    [InlineData(141)]
    [InlineData(142)]
    public void PredictionMemoryParserNeverThrowsAndRejectsLeaveNothing(int seed)
    {
        FakeTimeProvider clock = new(_predictionStart);
        TrendForecastProbe probe = new("disk.trend", clock, 200, ThresholdDirection.Rising);
        for (int i = 0; i < 40; i++)
        {
            probe.Record(10 + i);
            clock.Advance(TimeSpan.FromHours(1));
        }

        byte[] valid = PredictionMemoryCodec.ToUtf8Bytes(probe.ToMemory());
        string[] names = [.. FuzzKit.WireNames, "version", "samples", "t", "v", "values", "window", "count", "baseline", "median", "mad"];

        FuzzKit.Run(
            "PredictionMemoryCodec.TryParse",
            seed,
            600,
            (random, i) => FuzzKit.Mixed(random, i, valid, names),
            input =>
            {
                bool ok = PredictionMemoryCodec.TryParse(input, out PredictionMemory? memory);
                Assert.Equal(ok, memory is not null);
            });
    }

    [Theory]
    [InlineData(151)]
    [InlineData(152)]
    public async Task IncidentParsersNeverThrowAndRejectsLeaveNothing(int seed)
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentResult opened = await controller.OpenAsync(
            IncidentHarness.AutoOpen(IncidentTrigger.CriticalProbeRed, "host.disk.free:data"), CancellationToken.None);
        byte[] valid = IncidentCodec.ToUtf8Bytes(opened.Incident!);
        string[] names = [.. FuzzKit.WireNames, "phase", "plan", "transitions", "freezeApplied", "items", "incidentId", "hash", "previousHash"];

        FuzzKit.Run(
            "IncidentCodec.TryParseRecord",
            seed,
            500,
            (random, i) => FuzzKit.Mixed(random, i, valid, names),
            input =>
            {
                bool ok = IncidentCodec.TryParseRecord(input, out IncidentRecord? record);
                Assert.Equal(ok, record is not null);
            });
        FuzzKit.Run(
            "IncidentCodec.TryParseManifest",
            seed,
            500,
            (random, i) => FuzzKit.Mixed(random, i, valid, names),
            input =>
            {
                bool ok = IncidentCodec.TryParseManifest(input, out EvidenceManifest? manifest);
                Assert.Equal(ok, manifest is not null);
            });
    }

    [Theory]
    [InlineData(161)]
    [InlineData(162)]
    public void RuntimeEnvelopeAndPointerParsersNeverThrow(int seed)
    {
        Dictionary<string, string> contents = Runtime.RuntimeTestSigning.SampleContents();
        byte[] envelope = Runtime.RuntimeTestSigning.SignedEnvelope(Runtime.RuntimeTestSigning.ManifestFor(RuntimeLayout.SlotA, contents));
        byte[] pointer = Runtime.RuntimeTestSigning.SignedPointer(RuntimeLayout.SlotA, envelope);
        string[] names = [.. FuzzKit.WireNames, "manifest", "keyId", "signature", "slotId", "manifestSha256", "files", "path", "sha256", "size", "buildId"];

        FuzzKit.Run(
            "RuntimeManifestCodec.ParseEnvelope",
            seed,
            700,
            (random, i) => FuzzKit.Mixed(random, i, envelope, names),
            input => RuntimeManifestCodec.ParseEnvelope(input));
        FuzzKit.Run(
            "RuntimeManifestCodec.ParsePointer",
            seed,
            700,
            (random, i) => FuzzKit.Mixed(random, i, pointer, names),
            input => RuntimeManifestCodec.ParsePointer(input));
    }
}
