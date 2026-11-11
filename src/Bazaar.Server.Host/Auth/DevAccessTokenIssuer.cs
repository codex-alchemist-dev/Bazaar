using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Components.Remote;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// DEV ONLY. Signs access tokens with HMAC-SHA256 under a random 32-byte key that is generated once per process. The key is never
/// persisted and never logged, so a restart invalidates every token it issued. The host registers this issuer only when
/// <c>Bazaar:Remote:DevIssuer:Enabled</c> is true and the host environment is Development (<c>RemoteAuthCompositionExtensions</c>).
/// </summary>
/// <remarks>
/// This is a development credential, not a production token. The production signing key belongs to the identity service (doc 14) and is
/// not implemented here. Token format: base64url(JSON <c>{"sub":deviceId,"hum":humanId,"exp":unix seconds}</c>) + "." + base64url(HMACSHA256(key, payloadBytes)).
/// </remarks>
public sealed class DevAccessTokenIssuer : IAccessTokenIssuer
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _time;

    public DevAccessTokenIssuer(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DevTokenPayload(deviceId, humanId, expiresAt.ToUnixTimeSeconds()), DevTokenJsonContext.Default.DevTokenPayload);
        var mac = HMACSHA256.HashData(_key, payload);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
    }

    /// <summary>
    /// Checks a token against this process's key and the clock. Returns false for a malformed token, a bad MAC or an expired token.
    /// A token issued by another process (another key) never verifies here.
    /// </summary>
    internal bool TryVerify(string token, out DevTokenPayload claims)
    {
        claims = null!;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        byte[] payload;
        byte[] mac;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[0]);
            mac = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_key, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) return false;

        DevTokenPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(payload, DevTokenJsonContext.Default.DevTokenPayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null || parsed.Exp <= _time.GetUtcNow().ToUnixTimeSeconds()) return false;
        claims = parsed;
        return true;
    }
}

/// <summary>The claims inside a dev access token. Wire names follow the token format in <see cref="DevAccessTokenIssuer"/>.</summary>
internal sealed record DevTokenPayload(
    [property: JsonPropertyName("sub")] Guid DeviceId,
    [property: JsonPropertyName("hum")] Guid HumanId,
    [property: JsonPropertyName("exp")] long Exp);

/// <summary>Source-generated JSON for the dev token payload (CLAUDE.md: System.Text.Json source generation).</summary>
[JsonSerializable(typeof(DevTokenPayload))]
internal sealed partial class DevTokenJsonContext : JsonSerializerContext;
