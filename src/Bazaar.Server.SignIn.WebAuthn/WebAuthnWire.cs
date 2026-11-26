using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.SignIn.WebAuthn;

/// <summary>The JSON the sign-in page sends for an assertion (<c>signin.js</c>): base64url fields, no padding.</summary>
internal sealed record AssertionEnvelope(string? Id, string? RawId, string? Type, AssertionResponse? Response);

internal sealed record AssertionResponse(string? ClientDataJSON, string? AuthenticatorData, string? Signature, string? UserHandle);

/// <summary>The JSON a registration client sends: the credential id and the attestation object, base64url.</summary>
internal sealed record AttestationEnvelope(string? Id, string? RawId, string? Type, AttestationResponse? Response);

internal sealed record AttestationResponse(string? ClientDataJSON, string? AttestationObject);

internal sealed record ClientData(string? Type, string? Challenge, string? Origin, bool? CrossOrigin);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AssertionEnvelope))]
[JsonSerializable(typeof(AttestationEnvelope))]
[JsonSerializable(typeof(ClientData))]
internal sealed partial class WebAuthnJson : JsonSerializerContext;

internal static class WebAuthnWire
{
    public const int MaxEnvelopeChars = 16 * 1024;
    public const int MaxClientDataBytes = 2048;
    public const byte FlagUserPresent = 0x01;
    public const byte FlagUserVerified = 0x04;
    public const byte FlagBackupEligible = 0x08;
    public const byte FlagBackupState = 0x10;
    public const byte FlagAttested = 0x40;
    public const byte FlagExtensions = 0x80;

    public static byte[]? Decode(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > (maxBytes * 4 / 3) + 4)
        {
            return null;
        }

        try
        {
            return Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>
    /// Checks the collected client data against the ceremony: type, the challenge the server issued, an allowed origin and no
    /// cross-origin framing. The challenge is compared as decoded bytes in constant time.
    /// </summary>
    public static bool ClientDataValid(byte[] clientDataJson, string expectedType, string expectedChallenge, WebAuthnOptions options)
    {
        if (clientDataJson.Length is 0 or > MaxClientDataBytes)
        {
            return false;
        }

        ClientData? data;
        try
        {
            data = JsonSerializer.Deserialize(clientDataJson, WebAuthnJson.Default.ClientData);
        }
        catch (JsonException)
        {
            return false;
        }

        if (data is null || data.Type != expectedType || data.CrossOrigin is true || !options.IsOriginAllowed(data.Origin))
        {
            return false;
        }

        var expected = Decode(expectedChallenge, 64);
        var actual = Decode(data.Challenge, 64);
        return expected is { Length: >= 16 } && actual is not null && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
