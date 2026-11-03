using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Agents.Turns.Context;

// The manifest is written as JSON with a fixed property order (the record declaration order below) and camelCase keys.
// It holds ids, version ids, SHA-256 hashes and budgets only, never item text (doc 06 "Context assembly").

/// <summary>One item in the manifest: its id, its version id and the SHA-256 of its text.</summary>
internal sealed record ContextManifestItem(string ItemId, string Version, string Sha256);

/// <summary>One section in the manifest, in fixed section order. <see cref="Tokens"/> is the estimate of the kept items.</summary>
internal sealed record ContextManifestSection(string Section, int Tokens, IReadOnlyList<ContextManifestItem> Items);

/// <summary>The manifest document. <see cref="Budgets"/> are the ceilings used for this turn.</summary>
internal sealed record ContextManifest(
    int SchemaVersion,
    Guid AgentId,
    Guid TurnId,
    string Role,
    ContextBudget Budgets,
    IReadOnlyList<ContextManifestSection> Sections);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(ContextManifest))]
internal sealed partial class ContextManifestJsonContext : JsonSerializerContext
{
}

/// <summary>Serializes a <see cref="ContextManifest"/> with source-generated JSON (no reflection).</summary>
internal static class ContextManifestJson
{
    /// <summary>The manifest schema version. Bump it when a property changes.</summary>
    public const int SchemaVersion = 1;

    public static string Serialize(ContextManifest manifest) =>
        JsonSerializer.Serialize(manifest, ContextManifestJsonContext.Default.ContextManifest);
}
