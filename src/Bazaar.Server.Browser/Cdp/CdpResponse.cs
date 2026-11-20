using System.Text.Json;

namespace Bazaar.Server.Browser.Cdp;

/// <summary>Reads Chrome DevTools Protocol replies from the guest channel.</summary>
public static class CdpResponse
{
    /// <summary>
    /// True when the reply's <c>id</c> equals <paramref name="expectedId"/> and it has no <c>error</c> member.
    /// Malformed JSON, a missing or non-integer id, or an unexpected shape returns false. Never throws.
    /// </summary>
    public static bool IsSuccess(ReadOnlySpan<byte> json, int expectedId)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var idMatches = false;
            var hasError = false;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("id"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    idMatches = reader.TokenType == JsonTokenType.Number
                        && reader.TryGetInt32(out var id)
                        && id == expectedId;
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    hasError = true;
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reader.Skip();
                }
                else
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reader.Skip();
                }
            }

            return idMatches && !hasError && reader.TokenType == JsonTokenType.EndObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
