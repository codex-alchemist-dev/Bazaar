using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// What the privileged volume service answers for one volume (doc 63 sections 3.1 and 4).
/// <see cref="Error"/> is set instead of the facts when the request was bad or the status could not be read.
/// </summary>
/// <param name="Volume">The canonical GUID the answer is about; the client rejects an answer for another volume.</param>
/// <param name="ConversionComplete">Conversion status is fully encrypted.</param>
/// <param name="ProtectionOn">Protection status is on.</param>
/// <param name="EncryptionMethod">The BitLocker method number; 7 is XTS-AES-256.</param>
/// <param name="Locked">The volume is locked.</param>
internal sealed record VolumeStatusReply(string? Volume, bool ConversionComplete, bool ProtectionOn, int EncryptionMethod, bool Locked, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VolumeStatusReply))]
internal sealed partial class VolumeStatusJsonContext : JsonSerializerContext;

/// <summary>
/// The wire format of the status pipe: one request line <c>status &lt;canonical guid&gt;</c>, one JSON response line, each ended by
/// a line feed and size-capped. Only that exact request form is accepted.
/// </summary>
internal static class VolumeStatusProtocol
{
    public const string DefaultPipeName = "bazaar-volume";
    public const string RequestVerb = "status ";

    /// <summary><c>status </c> plus 36 characters, with room for a carriage return and the line feed.</summary>
    public const int MaxRequestBytes = 64;

    public const int MaxResponseBytes = 4096;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static string FormatRequest(string canonicalGuid) => RequestVerb + canonicalGuid;

    /// <summary>True only for exactly <c>status</c>, one space, and a canonical lowercase GUID.</summary>
    public static bool TryParseRequest(string? line, out string canonicalGuid)
    {
        canonicalGuid = string.Empty;
        if (line is null || !line.StartsWith(RequestVerb, StringComparison.Ordinal))
            return false;

        var guid = line[RequestVerb.Length..];
        if (!VolumeGuid.IsCanonical(guid))
            return false;

        canonicalGuid = guid;
        return true;
    }

    public static string SerializeReply(VolumeStatusReply reply) =>
        JsonSerializer.Serialize(reply, VolumeStatusJsonContext.Default.VolumeStatusReply);

    public static VolumeStatusReply? ParseReply(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, VolumeStatusJsonContext.Default.VolumeStatusReply);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one line ended by a line feed (a preceding carriage return is dropped). Returns null at the end of the stream
    /// before any byte. Throws <see cref="InvalidDataException"/> when the line exceeds <paramref name="maxBytes"/> or the stream
    /// ends in the middle of a line, and so never buffers more than the cap.
    /// </summary>
    public static async ValueTask<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(Math.Min(maxBytes, 256));
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one.AsMemory(), cancellationToken) == 0)
            {
                if (bytes.Count == 0)
                    return null;

                throw new InvalidDataException("The line is not terminated.");
            }

            if (one[0] == (byte)'\n')
                break;

            if (bytes.Count >= maxBytes)
                throw new InvalidDataException("The line is longer than allowed.");

            bytes.Add(one[0]);
        }

        if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
            bytes.RemoveAt(bytes.Count - 1);

        return Encoding.UTF8.GetString([.. bytes]);
    }

    public static async ValueTask WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
