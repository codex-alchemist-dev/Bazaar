using System.Text.Json;

namespace Bazaar.Contracts.Memory;

/// <summary>
/// JSON options for memory tool contracts (docs/design/08-memory-system.md, section 2). Scope, kind, tier and origin
/// travel as plain strings so new values do not break older readers. Property casing follows the web defaults until
/// the wire format is decided (docs/design/DECISIONS-LOG.md).
/// </summary>
public static class MemoryJson
{
    /// <summary>Shared options for serializing memory tool contracts.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
