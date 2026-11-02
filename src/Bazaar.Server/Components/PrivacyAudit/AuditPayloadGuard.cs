using System.Text.Json.Nodes;

namespace Bazaar.Server.Components.PrivacyAudit;

/// <summary>
/// Redaction guard for audit payloads (doc 32 section 8, doc 15). A payload must never hold tokens, passwords, recovery
/// codes, key material or personal message text, so any key whose name contains one of the secret-like words is refused.
/// The check is case-insensitive and applies to nested objects and arrays too.
/// </summary>
public static class AuditPayloadGuard
{
    /// <summary>Words that mark a payload key as secret-like. A key containing any of them is rejected.</summary>
    public static IReadOnlyList<string> SecretLikeWords { get; } = ["token", "secret", "password", "pass", "pwd", "code", "key", "body", "bearer", "jwt", "otp", "pin", "cookie", "private", "auth"];

    /// <summary>Throws <see cref="ArgumentException"/> when any key in <paramref name="payload"/> is secret-like.</summary>
    public static void Ensure(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsureNode(payload, path: string.Empty);
    }

    private static void EnsureNode(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    var keyPath = path.Length == 0 ? name : $"{path}.{name}";
                    var word = SecretLikeWords.FirstOrDefault(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
                    if (word is not null)
                        throw new ArgumentException(
                            $"Audit payload key '{keyPath}' looks secret-like (contains '{word}'); secrets and personal text are never audited.");
                    EnsureNode(child, keyPath);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    EnsureNode(arr[i], $"{path}[{i}]");
                break;
        }
    }
}
