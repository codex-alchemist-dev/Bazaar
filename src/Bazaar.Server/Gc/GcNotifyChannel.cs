using System.Globalization;
using System.Text.Json;

namespace Bazaar.Server.Gc;

/// <summary>
/// The Postgres NOTIFY channel that carries GC wake hints, and its payload. A notification is only a hint that
/// <c>gc_id</c> has reached at least <c>seq</c>; the log table is the truth (docs/design/01, spike S3).
/// </summary>
internal static class GcNotifyChannel
{
    public const string Name = "gc";

    public static string FormatPayload(Guid gcId, long seq) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"gc\":\"{gcId:D}\",\"seq\":{seq}}}");

    public static bool TryParsePayload(string payload, out Guid gcId, out long seq)
    {
        gcId = Guid.Empty;
        seq = 0;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("gc", out var g) && g.TryGetGuid(out gcId)
                && root.TryGetProperty("seq", out var s) && s.TryGetInt64(out seq);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
