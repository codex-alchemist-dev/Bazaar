using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Telemetry;

/// <summary>
/// The closed set of log property keys that a crash report carries (doc 43, section 5.3). The set is this record: a key
/// that is not a property here is dropped when the report is read, and a value is a typed scalar, so an object or an array
/// cannot be stored. Adding a key is a schema change; it needs a new entry here and in <see cref="AllowedKeys"/>.
/// </summary>
public sealed record CrashLogProperties(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Attempt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Module,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode);

/// <summary>The wire names of the allowed log property keys, in declaration order.</summary>
public static class CrashLogPropertyKeys
{
    /// <summary>Every key a crash report may carry. Tests check this against the JSON names of <see cref="CrashLogProperties"/>.</summary>
    public static IReadOnlyList<string> AllowedKeys { get; } =
    [
        "attempt",
        "count",
        "duration_ms",
        "status",
        "module",
        "error_code",
    ];
}
