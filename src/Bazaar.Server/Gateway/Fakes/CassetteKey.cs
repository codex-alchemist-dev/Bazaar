using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// The fields a cassette is matched on (docs/design/34-testing-strategy.md). Each part is a hash, so a mismatch
/// diff names the part that changed without storing prompt text in the key. <see cref="Digest"/> covers all of them.
/// </summary>
public sealed record CassetteKey(
    string Provider,
    string Model,
    string SystemHash,
    string MessagesDigest,
    string ToolsDigest,
    string SamplingDigest)
{
    /// <summary>The field names, in the order <see cref="Differences"/> reports them.</summary>
    public static IReadOnlyList<string> FieldNames { get; } =
        ["provider", "model", "system_hash", "messages_digest", "tools_digest", "sampling_digest"];

    /// <summary>
    /// Builds the key for one request. The agent id, route and turn id are not part of the key: a cassette
    /// records what the model was asked, not who asked. <paramref name="model"/> is the resolved model id.
    /// </summary>
    public static CassetteKey For(string provider, string model, ModelRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(request);

        var system = request.Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text).ToArray();
        var conversation = request.Messages
            .Where(m => m.Role != ModelRole.System)
            .Select(m => new MessageShape(
                m.Role.ToString().ToLowerInvariant(),
                m.Text,
                m.ToolCallId,
                m.ToolCalls?.Select(c => new ToolCallShape(c.Id, c.Name, c.ArgumentsJson)).ToArray()))
            .ToArray();
        var tools = request.Tools
            .Select(t => new ToolShape(t.Name, t.Description, t.ParametersJsonSchema))
            .ToArray();
        var sampling = new SamplingShape(request.MaxOutputTokens, request.Temperature);

        return new CassetteKey(
            provider,
            model,
            Hash(JsonSerializer.Serialize(system)),
            Hash(JsonSerializer.Serialize(conversation)),
            Hash(JsonSerializer.Serialize(tools)),
            Hash(JsonSerializer.Serialize(sampling)));
    }

    /// <summary>The request digest: a SHA-256 over every key field, in a fixed order and as a JSON array.</summary>
    public string Digest() => Hash(JsonSerializer.Serialize(Parts()));

    /// <summary>The key fields in <see cref="FieldNames"/> order.</summary>
    public IReadOnlyList<string> Parts() => [Provider, Model, SystemHash, MessagesDigest, ToolsDigest, SamplingDigest];

    /// <summary>The names of the fields that differ from <paramref name="other"/>, in <see cref="FieldNames"/> order.</summary>
    public IReadOnlyList<string> Differences(CassetteKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var mine = Parts();
        var theirs = other.Parts();
        var names = new List<string>();
        for (var i = 0; i < mine.Count; i++)
        {
            if (!string.Equals(mine[i], theirs[i], StringComparison.Ordinal))
            {
                names.Add(FieldNames[i]);
            }
        }

        return names;
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // Provider tokens (ModelToolCall.ProviderData) are left out on purpose: they are opaque and may differ between runs.
    internal sealed record MessageShape(string Role, string Text, string? ToolCallId, IReadOnlyList<ToolCallShape>? ToolCalls);

    internal sealed record ToolCallShape(string Id, string Name, string ArgumentsJson);

    internal sealed record ToolShape(string Name, string Description, string ParametersJsonSchema);

    internal sealed record SamplingShape(int? MaxOutputTokens, double? Temperature);
}
