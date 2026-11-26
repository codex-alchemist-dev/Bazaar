using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Gc;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.EngineGcRecorder.Tests;

public sealed class GcEngineCallRecorderTests
{
    private sealed class Writer : IGcLogWriter
    {
        public List<AppendRequest> Appended { get; } = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            Appended.Add(request);
            return Task.FromResult(new AppendResult(request.GcId, 1, Guid.NewGuid(), false, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static EngineToolCallRecord Record(Guid? gc) =>
        new(Guid.NewGuid(), "roblox", "multi_edit", RiskClass.Write, "grant-1", true, EngineDispatchReason.Allowed, "abc123") { GcId = gc };

    [Fact]
    public async Task Writes_a_system_event_with_ids_and_the_args_hash_only()
    {
        var writer = new Writer();
        var gc = Guid.NewGuid();

        await new GcEngineCallRecorder(writer).RecordAsync(Record(gc), TestContext.Current.CancellationToken);

        var request = Assert.Single(writer.Appended);
        Assert.Equal(gc, request.GcId);
        Assert.Equal(GcEventKinds.System, request.Kind);
        Assert.Equal(AuthorKind.System, request.AuthorKind);
        using var json = JsonDocument.Parse(request.PayloadJson!);
        var root = json.RootElement;
        Assert.Equal("engine_call", root.GetProperty("kind").GetString());
        Assert.Equal("multi_edit", root.GetProperty("tool").GetString());
        Assert.Equal("write", root.GetProperty("risk_class").GetString());
        Assert.True(root.GetProperty("allowed").GetBoolean());
        Assert.Equal("abc123", root.GetProperty("args_sha256").GetString());
        Assert.False(root.TryGetProperty("args", out _));
    }

    [Fact]
    public async Task Throws_when_no_group_chat_is_named_so_the_call_is_denied()
    {
        var writer = new Writer();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GcEngineCallRecorder(writer).RecordAsync(Record(null), TestContext.Current.CancellationToken));
        Assert.Empty(writer.Appended);
    }
}
