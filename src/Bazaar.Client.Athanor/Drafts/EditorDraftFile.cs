using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.Athanor.Drafts;

/// <summary>The on-disk shape of one draft (doc 52 §5). Kept separate from <see cref="EditorDraft"/> so the file format can change without changing the store contract.</summary>
internal sealed record EditorDraftFile(string WorkspaceId, string Path, string Content, string BaseRevision, DateTimeOffset SavedAt);

/// <summary>Source-generated JSON for draft files (no reflection at run time).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EditorDraftFile))]
internal sealed partial class EditorDraftJsonContext : JsonSerializerContext;
