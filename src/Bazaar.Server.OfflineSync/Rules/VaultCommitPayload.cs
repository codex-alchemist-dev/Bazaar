using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>
/// Payload of a Vault commit push intent (doc 56 section 5, item 8). It names what the device wants to land; the git
/// mechanics belong to the Vault module.
/// </summary>
/// <param name="Device">The device that made the commit. Must equal the write's device.</param>
/// <param name="Branch">The branch the device pushed.</param>
/// <param name="HeadCommit">The full lowercase SHA of the commit the device wants to land.</param>
/// <param name="BaseCommit">The full lowercase SHA of the main-branch commit the device started from.</param>
public sealed record VaultCommitPayload(Guid Device, string Branch, string HeadCommit, string BaseCommit);

/// <summary>JSON source generation context for the Vault commit payload. AOT safe, no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(VaultCommitPayload))]
public sealed partial class VaultCommitPayloadJsonContext : JsonSerializerContext
{
}

/// <summary>Decodes Vault commit payloads.</summary>
public static class VaultCommitPayloadCodec
{
    /// <summary>Decodes a payload. Returns null for malformed bytes instead of throwing.</summary>
    /// <param name="bytes">The payload bytes.</param>
    /// <returns>The payload, or null when the bytes are not a valid payload.</returns>
    public static VaultCommitPayload? Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes.Span, VaultCommitPayloadJsonContext.Default.VaultCommitPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
