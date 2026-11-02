using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// The hash stored as <c>turn_step.request_hash</c> for a model call (doc 06, "Concurrency and recovery"). It covers the
/// route, every message and every tool definition, so two calls with the same hash send the same request. Each field is
/// length-prefixed, so no two different requests can build the same string.
/// </summary>
public static class ModelRequestHash
{
    /// <summary>Returns <c>sha256:</c> and the lowercase hex digest of the request.</summary>
    public static string Compute(string route, IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelToolDefinition> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var sb = new StringBuilder();
        Append(sb, route);
        sb.Append(messages.Count).Append(';');
        foreach (var message in messages)
        {
            Append(sb, message.Role.ToString());
            Append(sb, message.Text);
            Append(sb, message.ToolCallId ?? "");
            var calls = message.ToolCalls ?? [];
            sb.Append(calls.Count).Append(';');
            foreach (var call in calls)
            {
                Append(sb, call.Id);
                Append(sb, call.Name);
                Append(sb, call.ArgumentsJson);
            }
        }

        sb.Append(tools.Count).Append(';');
        foreach (var tool in tools)
        {
            Append(sb, tool.Name);
            Append(sb, tool.Description);
            Append(sb, tool.ParametersJsonSchema);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void Append(StringBuilder sb, string value) => sb.Append(value.Length).Append(':').Append(value).Append(';');
}
