using System.Text.Json;

namespace Bazaar.Server.Memory.Consolidation;

/// <summary>One operation as the model proposed it, before validation. Missing fields are null.</summary>
/// <param name="Op">Operation name: <c>create_semantic</c>, <c>rewrite_lesson</c> or <c>deprecate</c>.</param>
/// <param name="TargetKind">For <c>deprecate</c>: the kind of the note to deprecate.</param>
/// <param name="Slug">File name without <c>.md</c>.</param>
/// <param name="Title">Front matter title.</param>
/// <param name="Body">Note body.</param>
/// <param name="Reason">For <c>deprecate</c>: why.</param>
/// <param name="ReplacedBy">For <c>deprecate</c>: the note that replaces it.</param>
public sealed record ProposedOperation(
    string Op,
    string? TargetKind,
    string? Slug,
    string? Title,
    string? Body,
    string? Reason,
    string? ReplacedBy);

/// <summary>A parsed model reply.</summary>
/// <param name="Operations">Proposed operations in reply order.</param>
/// <param name="MaintenanceReport">The reply's maintenance text, or null when absent.</param>
public sealed record ConsolidationReply(IReadOnlyList<ProposedOperation> Operations, string? MaintenanceReport);

/// <summary>
/// Parses the reader session's reply. The reply must be one JSON object with an <c>operations</c> array and an
/// optional <c>maintenance_report</c> string. Each operation must be an object with a string <c>op</c>; other
/// fields are read as strings when present. Anything else is a malformed reply. Pure, and it never throws.
/// </summary>
/// <remarks>
/// Schema:
/// <code>
/// {"operations":[
///   {"op":"create_semantic","slug":"...","title":"...","body":"..."},
///   {"op":"rewrite_lesson","slug":"...","title":"...","body":"..."},
///   {"op":"deprecate","target_kind":"procedural","slug":"...","reason":"...","replaced_by":"..."}],
///  "maintenance_report":"..."}
/// </code>
/// </remarks>
public static class ConsolidationReplyParser
{
    /// <summary>Tries to parse a reply. On failure <paramref name="detail"/> says why.</summary>
    public static bool TryParse(string text, out ConsolidationReply? reply, out string? detail)
    {
        reply = null;
        detail = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            detail = "The reply is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                detail = "The reply is not a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            {
                detail = "The reply has no operations array.";
                return false;
            }

            var proposed = new List<ProposedOperation>();
            foreach (var item in operations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !TryString(item, "op", out var op) || op is null)
                {
                    detail = "An operation is not an object with a string op.";
                    return false;
                }

                proposed.Add(new ProposedOperation(
                    op,
                    ReadString(item, "target_kind"),
                    ReadString(item, "slug"),
                    ReadString(item, "title"),
                    ReadString(item, "body"),
                    ReadString(item, "reason"),
                    ReadString(item, "replaced_by")));
            }

            var maintenance = ReadString(root, "maintenance_report");
            reply = new ConsolidationReply(proposed, maintenance);
            return true;
        }
        catch (JsonException)
        {
            detail = "The reply is not valid JSON.";
            return false;
        }
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
