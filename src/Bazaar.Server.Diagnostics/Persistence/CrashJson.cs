using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Grouping;

namespace Bazaar.Server.Diagnostics.Persistence;

/// <summary>
/// Hand-written JSON for the two jsonb columns (issue facts and outbox payloads). No reflection, so the module stays trimmable,
/// and the shape is pinned here rather than by a serializer's defaults.
/// </summary>
internal static class CrashJson
{
    public const string KindFileIssue = "file_issue";
    public const string KindAddComment = "add_comment";
    public const string KindReopen = "reopen";

    public static string WriteFacts(CrashIssueFacts facts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (facts.ErrorCode is null) { w.WriteNull("errorCode"); } else { w.WriteString("errorCode", facts.ErrorCode); }
            w.WriteString("errorType", facts.ErrorType);
            w.WriteStartArray("frames");
            foreach (var f in facts.Frames)
            {
                w.WriteStartObject();
                w.WriteString("module", f.ModuleId);
                w.WriteString("type", f.Type);
                w.WriteString("method", f.Method);
                w.WriteBoolean("app", f.App);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (facts.FlaggedLine is { } line) { w.WriteNumber("flaggedLine", line); } else { w.WriteNull("flaggedLine"); }
            if (facts.RedactedMessage is null) { w.WriteNull("message"); } else { w.WriteString("message", facts.RedactedMessage); }
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static CrashIssueFacts ReadFacts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var frames = ImmutableArray.CreateBuilder<FingerprintFrame>();
        foreach (var f in root.GetProperty("frames").EnumerateArray())
        {
            frames.Add(new FingerprintFrame(
                f.GetProperty("module").GetString()!,
                f.GetProperty("type").GetString()!,
                f.GetProperty("method").GetString()!,
                f.GetProperty("app").GetBoolean()));
        }

        return new CrashIssueFacts(
            OptString(root, "errorCode"),
            root.GetProperty("errorType").GetString()!,
            frames.ToImmutable(),
            root.TryGetProperty("flaggedLine", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
            OptString(root, "message"));
    }

    /// <summary>Returns the outbox kind and payload JSON for an action the store can persist. Other actions are not outbox work.</summary>
    public static (string Kind, string Payload) WriteAction(GroupAction action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        string kind;
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            switch (action)
            {
                case FileIssue:
                    kind = KindFileIssue;
                    break;
                case AddComment c:
                    kind = KindAddComment;
                    w.WriteString("reason", c.Reason.ToString());
                    w.WriteNumber("count", c.OccurrenceCount);
                    w.WriteString("text", c.Text);
                    break;
                case Reopen r:
                    kind = KindReopen;
                    w.WriteString("fixedIn", r.FixedInVersion);
                    w.WriteString("seenIn", r.OccurrenceVersion);
                    w.WriteString("text", r.Text);
                    break;
                default:
                    throw new ArgumentException($"{action.GetType().Name} is not outbox work.", nameof(action));
            }

            w.WriteEndObject();
        }

        return (kind, System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static GroupAction ReadAction(string kind, string fingerprint, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        return kind switch
        {
            KindFileIssue => new FileIssue(fingerprint),
            KindAddComment => new AddComment(
                Enum.Parse<CommentReason>(root.GetProperty("reason").GetString()!),
                root.GetProperty("count").GetInt64(),
                root.GetProperty("text").GetString()!),
            KindReopen => new Reopen(
                root.GetProperty("fixedIn").GetString()!,
                root.GetProperty("seenIn").GetString()!,
                root.GetProperty("text").GetString()!),
            _ => throw new InvalidOperationException($"Unknown outbox kind '{kind}'."),
        };
    }

    private static string? OptString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
