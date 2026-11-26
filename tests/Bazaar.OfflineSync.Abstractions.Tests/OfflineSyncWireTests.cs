using System.Text;
using System.Text.Json;
using Bazaar.OfflineSync;

namespace Bazaar.OfflineSync.Abstractions.Tests;

public sealed class OfflineSyncWireTests
{
    private static readonly Guid _device = Guid.Parse("6f1d0a52-8f7e-4c3b-9a71-2d5e4b8c9f01");

    private static OfflineWrite Write(long sequence, byte[] payload) => new(
        Guid.NewGuid(), _device, sequence, OfflineDataClass.KanbanField, "task-1", "v3", payload, new Hlc(1_700_000_000_000, 4, _device));

    [Fact]
    public void Batch_round_trips_through_json()
    {
        var batch = new OfflineWriteBatch(_device, [Write(1, [1, 2, 3]), Write(2, [])]);

        var json = JsonSerializer.SerializeToUtf8Bytes(OfflineSyncWire.ToDto(batch), OfflineSyncWireContext.Default.OfflineBatchRequestDto);
        var back = OfflineSyncWire.ToBatch(JsonSerializer.Deserialize(json, OfflineSyncWireContext.Default.OfflineBatchRequestDto));

        Assert.NotNull(back);
        Assert.Equal(batch.DeviceId, back.DeviceId);
        Assert.Equal(2, back.Writes.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(batch.Writes[i].WriteId, back.Writes[i].WriteId);
            Assert.Equal(batch.Writes[i].DeviceSequence, back.Writes[i].DeviceSequence);
            Assert.Equal(batch.Writes[i].DataClass, back.Writes[i].DataClass);
            Assert.Equal(batch.Writes[i].Target, back.Writes[i].Target);
            Assert.Equal(batch.Writes[i].BaseVersion, back.Writes[i].BaseVersion);
            Assert.Equal(batch.Writes[i].Hlc, back.Writes[i].Hlc);
            Assert.True(batch.Writes[i].Payload.Span.SequenceEqual(back.Writes[i].Payload.Span));
        }
    }

    [Fact]
    public void Wire_uses_snake_case_names_and_enum_text()
    {
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            OfflineSyncWire.ToDto(new OfflineWriteBatch(_device, [Write(1, [9])])), OfflineSyncWireContext.Default.OfflineBatchRequestDto));

        Assert.Contains("\"device_sequence\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"data_class\":\"KanbanField\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":\"CQ==\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_round_trips_including_gap_and_reason()
    {
        var id = Guid.NewGuid();
        var result = new OfflineBatchResult(
            [new OfflineWriteOutcome(id, OfflineOutcomeKind.Rejected, "v9", "forbidden transition")], 6, 7);

        var json = JsonSerializer.SerializeToUtf8Bytes(OfflineSyncWire.ToDto(result), OfflineSyncWireContext.Default.OfflineBatchResponseDto);
        var back = OfflineSyncWire.ToResult(JsonSerializer.Deserialize(json, OfflineSyncWireContext.Default.OfflineBatchResponseDto)!);

        Assert.Equal(6, back.AckedSequence);
        Assert.Equal(7, back.GapAtSequence);
        Assert.Equal(new OfflineWriteOutcome(id, OfflineOutcomeKind.Rejected, "v9", "forbidden transition"), Assert.Single(back.Outcomes));
    }

    [Fact]
    public void Invalid_base64_payload_gives_null_batch()
    {
        var dto = OfflineSyncWire.ToDto(new OfflineWriteBatch(_device, [Write(1, [1])]));
        var bad = dto with { Writes = [dto.Writes[0] with { PayloadBase64 = "not base64!" }] };

        Assert.Null(OfflineSyncWire.ToBatch(bad));
        Assert.Null(OfflineSyncWire.ToBatch(null));
    }
}
