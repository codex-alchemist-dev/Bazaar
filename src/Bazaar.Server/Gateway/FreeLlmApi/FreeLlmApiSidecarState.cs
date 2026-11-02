using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Creates and reads the sidecar's private files: the encryption key, the admin password, the declarative config FreeLLMAPI
/// applies at start, and the unified key the gateway reads. Secret values are never logged or returned in messages.
/// </summary>
public static class FreeLlmApiSidecarState
{
    public const string EncryptionKeyFile = "encryption-key";
    public const string AdminPasswordFile = "admin-password";
    public const string AdminCreatedMarker = "admin-created";
    public const string DeclarativeConfigFile = "declarative-config.json";
    public const string AdminEmail = "bazaar-sidecar@localhost";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Returns the 32-byte encryption key as 64 hex characters, creating it on first use.</summary>
    public static string EnsureEncryptionKey(string stateDirectory) =>
        ReadOrCreateSecret(Path.Combine(stateDirectory, EncryptionKeyFile), () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());

    /// <summary>Returns the sidecar admin password, creating it on first use.</summary>
    public static string EnsureAdminPassword(string stateDirectory) =>
        ReadOrCreateSecret(Path.Combine(stateDirectory, AdminPasswordFile), () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Reads the provider keys file when it exists. A missing file means no upstream keys yet, which is not an error.</summary>
    public static IReadOnlyList<ProviderKeyEntry> ReadProviderKeys(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = JsonSerializer.Deserialize<List<ProviderKeyEntry>>(File.ReadAllText(path), _json)
            ?? throw new InvalidDataException($"Provider keys file '{path}' must be a JSON array.");
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Platform) || string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new InvalidDataException($"Provider keys file '{path}' has an entry without a platform or key. Values are not shown.");
            }
        }

        return entries;
    }

    /// <summary>
    /// Writes the declarative config FreeLLMAPI applies at start. The admin entry is included only until the first successful
    /// start; the marker file records that the account exists.
    /// </summary>
    public static string WriteDeclarativeConfig(string stateDirectory, IReadOnlyList<ProviderKeyEntry> keys)
    {
        var markerPath = Path.Combine(stateDirectory, AdminCreatedMarker);
        var config = new Dictionary<string, object?> { ["keys"] = keys };
        if (!File.Exists(markerPath))
        {
            config["admin"] = new { email = AdminEmail, password = EnsureAdminPassword(stateDirectory) };
        }

        var path = Path.Combine(stateDirectory, DeclarativeConfigFile);
        WriteSecretFile(path, JsonSerializer.Serialize(config, _json));
        return path;
    }

    /// <summary>Records that the admin account exists and removes the password from the declarative config.</summary>
    public static void MarkAdminCreated(string stateDirectory)
    {
        File.WriteAllText(Path.Combine(stateDirectory, AdminCreatedMarker), DateTimeOffset.UtcNow.ToString("O"));
        var path = Path.Combine(stateDirectory, DeclarativeConfigFile);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes the unified key the gateway reads through its <c>file:</c> reference.</summary>
    public static void WriteUnifiedKey(string path, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        WriteSecretFile(path, key);
    }

    private static string ReadOrCreateSecret(string path, Func<string> create)
    {
        if (File.Exists(path))
        {
            return File.ReadAllText(path).Trim();
        }

        var value = create();
        WriteSecretFile(path, value);
        return value;
    }

    /// <summary>Writes a file readable by the owner only on Unix. On Windows it relies on the state directory's ACL.</summary>
    private static void WriteSecretFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>One upstream provider key for the declarative config. Serialised as <c>platform</c>, <c>key</c>, <c>label</c>.</summary>
public sealed record ProviderKeyEntry(string Platform, string Key, string? Label = null);
