using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;

namespace Bazaar.Server.BrowserPool.Display;

/// <summary>What the reader loop needs from a reply without building a document: its id and whether it is an error.</summary>
/// <param name="HasId">Whether the message carries an <c>id</c>. Events and other messages without one are dropped.</param>
/// <param name="Id">The request id.</param>
/// <param name="IsError">Whether the message has an <c>error</c> member.</param>
/// <param name="ErrorCode">The numeric <c>error.code</c>, or 0.</param>
internal readonly record struct CdpEnvelope(bool HasId, long Id, bool IsError, int ErrorCode);

/// <summary>
/// Hand-written JSON for the few DevTools shapes this module sends and reads. It uses <see cref="Utf8JsonWriter"/>,
/// <see cref="Utf8JsonReader"/> and <see cref="JsonDocument"/> only, so nothing here depends on reflection or trimming.
/// </summary>
internal static class CdpJson
{
    private static readonly JsonReaderOptions _readerOptions = new() { MaxDepth = 128 };

    /// <summary>The PNG file signature.</summary>
    public static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Renders <c>{"id":..,"method":"..","params":..,"sessionId":".."}</c>. <paramref name="paramsJson"/> must be one JSON value.</summary>
    public static byte[] BuildRequest(long id, CdpMethod method, string? cdpSessionId, ReadOnlySpan<byte> paramsJson)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + paramsJson.Length);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id"u8, id);
            w.WriteString("method"u8, CdpMethods.Name(method));
            if (!paramsJson.IsEmpty)
            {
                w.WritePropertyName("params"u8);
                w.WriteRawValue(paramsJson);
            }

            if (cdpSessionId is not null)
            {
                w.WriteString("sessionId"u8, cdpSessionId);
            }

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the envelope of a message. Returns false when the bytes are not a JSON object.</summary>
    public static bool TryScanEnvelope(ReadOnlySpan<byte> message, out CdpEnvelope envelope)
    {
        envelope = default;
        try
        {
            var r = new Utf8JsonReader(message, _readerOptions);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var hasId = false;
            long id = 0;
            var isError = false;
            var code = 0;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("id"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.Number && r.TryGetInt64(out id))
                    {
                        hasId = true;
                    }
                    else
                    {
                        r.Skip();
                    }
                }
                else if (r.ValueTextEquals("error"u8))
                {
                    isError = true;
                    r.Read();
                    code = ReadErrorCode(ref r);
                }
                else
                {
                    r.Read();
                    r.Skip();
                }
            }

            envelope = new CdpEnvelope(hasId, id, isError, code);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds <c>result.data</c> (a base64 string) in a <c>Page.captureScreenshot</c> reply, without allocating a string for it.
    /// Returns the byte range of the text inside <paramref name="message"/>. False when it is missing, not a string, or escaped
    /// (the browser never escapes base64).
    /// </summary>
    public static bool TryFindScreenshotData(byte[] message, out int start, out int length)
    {
        start = 0;
        length = 0;
        try
        {
            var r = new Utf8JsonReader(message, _readerOptions);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (!r.ValueTextEquals("result"u8))
                {
                    r.Read();
                    r.Skip();
                    continue;
                }

                r.Read();
                if (r.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (!r.ValueTextEquals("data"u8))
                    {
                        r.Read();
                        r.Skip();
                        continue;
                    }

                    r.Read();
                    if (r.TokenType != JsonTokenType.String || r.ValueIsEscaped || r.HasValueSequence)
                    {
                        return false;
                    }

                    length = r.ValueSpan.Length;
                    start = checked((int)r.TokenStartIndex) + 1;
                    return true;
                }

                return false;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Decodes base64 text. False when it is not valid base64.</summary>
    public static bool TryDecodeBase64(ReadOnlySpan<byte> text, out byte[] decoded)
    {
        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
        if (Base64.DecodeFromUtf8(text, buffer, out _, out var written) != OperationStatus.Done)
        {
            decoded = [];
            return false;
        }

        decoded = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
        return true;
    }

    private static int ReadErrorCode(ref Utf8JsonReader r)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return 0;
        }

        var code = 0;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("code"u8))
            {
                r.Read();
                if (r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out code))
                {
                    code = 0;
                    r.Skip();
                }
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        return code;
    }
}
