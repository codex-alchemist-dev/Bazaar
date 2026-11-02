using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

/// <summary>
/// Test-only <see cref="ISettingStore"/> with the same write rules as <see cref="NpgsqlSettingStore"/>: a write is
/// evaluated, audited unless it repeats the stored value, and stored only when accepted. Nothing is durable.
/// </summary>
internal sealed class InMemorySettingStore(SettingRegistry registry, ISettingAuditSink audit) : ISettingStore
{
    /// <summary>Same wire form as the server's settings JSON: snake_case enum names, no integers for enums.</summary>
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    private readonly Dictionary<(string Key, string ScopeId), string> _rows = [];

    /// <summary>The canonical stored JSON for a key and scope id, or null when nothing is stored.</summary>
    public string? StoredJson(string key, string scopeId = "") =>
        _rows.TryGetValue((key, scopeId), out var json) ? json : null;

    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_rows.TryGetValue((key.Name, scopeId), out var json))
            return Task.FromResult(key.Default);

        using var doc = JsonDocument.Parse(json);
        return Task.FromResult(key.Read(doc.RootElement));
    }

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var proposed = JsonSerializer.SerializeToDocument(value, _options);
        return Task.FromResult(Write(key, proposed.RootElement, actor, scopeId));
    }

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!registry.TryGet(key, out var definition) || definition is null)
            return Task.FromResult(new SettingWriteResult(key, SettingWriteOutcome.Rejected, ApprovalTier.None, $"'{key}' is not a registered setting."));

        return Task.FromResult(Write(definition, value, actor, scopeId));
    }

    private SettingWriteResult Write(SettingDefinition definition, JsonElement proposed, SettingActor actor, string scopeId)
    {
        if (!registry.Contains(definition))
            return new SettingWriteResult(definition.Name, SettingWriteOutcome.Rejected, ApprovalTier.None, $"'{definition.Name}' is not a registered setting.");

        var evaluation = definition.Evaluate(actor, proposed);
        var result = new SettingWriteResult(definition.Name, evaluation.Outcome, evaluation.ApprovalTier, evaluation.Reason);
        var stored = StoredJson(definition.Name, scopeId);

        if (evaluation.Outcome == SettingWriteOutcome.Accepted && stored == evaluation.ValueJson)
            return result;

        audit.AppendAsync(
            new SettingChangeAudit(
                definition.Name,
                definition.Scope,
                scopeId,
                actor.Id,
                evaluation.Outcome,
                OldValueJson: stored ?? definition.DefaultValueJson,
                NewValueJson: evaluation.ValueJson,
                evaluation.Reason)).GetAwaiter().GetResult();

        if (evaluation.Outcome == SettingWriteOutcome.Accepted)
            _rows[(definition.Name, scopeId)] = evaluation.ValueJson;

        return result;
    }
}
