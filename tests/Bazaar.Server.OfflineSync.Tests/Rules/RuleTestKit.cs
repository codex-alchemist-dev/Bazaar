using System.Text.Json;
using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

/// <summary>Shared builders and fakes for the rule tests.</summary>
internal static class RuleTestKit
{
    public static readonly Guid DeviceA = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid DeviceB = new("bbbbbbbb-0000-0000-0000-000000000002");

    public static Hlc At(long wall, int counter = 0, Guid? device = null) => new(wall, counter, device ?? DeviceA);

    public static OfflineWrite Write(OfflineDataClass dataClass, string target, ReadOnlyMemory<byte> payload, string? baseVersion = null, Guid? writeId = null, Hlc? hlc = null)
    {
        var stamp = hlc ?? At(1000);
        return new OfflineWrite(writeId ?? Guid.NewGuid(), stamp.DeviceId, 1, dataClass, target, baseVersion, payload, stamp);
    }

    public static ReadOnlyMemory<byte> Json(GcMessagePayload value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, RulePayloadJsonContext.Default.GcMessagePayload);

    public static ReadOnlyMemory<byte> Json(KanbanFieldPayload value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, RulePayloadJsonContext.Default.KanbanFieldPayload);

    public static ReadOnlyMemory<byte> Json(KanbanEdgePayload value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, RulePayloadJsonContext.Default.KanbanEdgePayload);

    public static ReadOnlyMemory<byte> Json(ConstellationPositionPayload value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, RulePayloadJsonContext.Default.ConstellationPositionPayload);

    public static ReadOnlyMemory<byte> Json(PreferencePayload value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, RulePayloadJsonContext.Default.PreferencePayload);

    public static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    public static void Shuffle<T>(IList<T> items, Random random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>Records every review item opened.</summary>
internal sealed class RecordingReviewSink : IReviewSink
{
    private readonly List<ReviewItem> _items = [];

    /// <summary>The review items, in the order opened.</summary>
    public IReadOnlyList<ReviewItem> Items => _items;

    /// <inheritdoc />
    public ValueTask OpenAsync(ReviewItem item, CancellationToken cancellationToken)
    {
        _items.Add(item);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Allows only the listed transitions. A null "from" status may only enter Backlog.</summary>
internal sealed class FakeTransitionChecker : ITransitionChecker
{
    private static readonly HashSet<(string From, string To)> _allowed =
    [
        ("Backlog", "Ready"),
        ("Ready", "In progress"),
        ("In progress", "Review"),
        ("Review", "Done"),
        ("Review", "In progress"),
    ];

    /// <inheritdoc />
    public ValueTask<TransitionDecision> CheckAsync(string taskId, string? fromStatus, string toStatus, CancellationToken cancellationToken)
    {
        var from = fromStatus ?? "Backlog";
        if (fromStatus is null && toStatus == "Backlog")
        {
            return ValueTask.FromResult(new TransitionDecision(true));
        }

        return ValueTask.FromResult(_allowed.Contains((from, toStatus))
            ? new TransitionDecision(true)
            : new TransitionDecision(false, $"Transition {from} -> {toStatus} is not allowed."));
    }
}

/// <summary>Marks a fixed set of keys as safety-relevant.</summary>
internal sealed class FixedSafetyPolicy(params string[] keys) : ISafetyKeyPolicy
{
    private readonly HashSet<string> _keys = [.. keys];

    /// <inheritdoc />
    public bool IsSafetyRelevant(string key) => _keys.Contains(key);
}
