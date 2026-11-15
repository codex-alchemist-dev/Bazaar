using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// RFC 3161 time-stamp authority target (design doc 48, section 5.3). Sends a TimeStampReq over an HTTP POST to the configured
/// TSA endpoint and keeps the TimeStampToken of a granted response as the receipt.
/// </summary>
/// <remarks>
/// Only the SHA-256 digest and a random nonce are sent. A response is accepted only when its TSTInfo carries the same digest
/// and nonce. The token's signature and certificate chain are not checked here: the verifier checks the token offline
/// against the TSA certificate. <see cref="VerifyAsync"/> checks the digest inside the stored token.
/// </remarks>
public sealed class Rfc3161Target : IAuditAnchorTarget
{
    /// <summary>Stable target identifier. Named in receipts and in audit events.</summary>
    public const string Id = "rfc3161";

    /// <summary>Largest response accepted from a TSA. A token with a certificate chain is a few kilobytes.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    private const string _requestMediaType = "application/timestamp-query";
    private const string _replyMediaType = "application/timestamp-reply";
    private const string _sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string _signedDataOid = "1.2.840.113549.1.7.2";
    private const string _tstInfoOid = "1.2.840.113549.1.9.16.1.4";

    private const int _digestSize = SHA256.HashSizeInBytes;
    private const int _nonceSize = 8;

    /// <summary>AlgorithmIdentifier for SHA-256: SEQUENCE { OID, NULL }. Compared byte for byte with the token's value.</summary>
    private static readonly byte[] _sha256Algorithm = AsnDer.Sequence(AsnDer.ObjectIdentifier(_sha256Oid), AsnDer.Null());

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly TimeProvider _time;
    private readonly Func<byte[]> _nonceSource;

    /// <summary>
    /// Creates the target. Throws <see cref="ArgumentException"/> when the options do not name a usable endpoint.
    /// <paramref name="nonceSource"/> returns the nonce for each request; the default is random. A fixed source is for tests only.
    /// </summary>
    public Rfc3161Target(HttpClient httpClient, Rfc3161TargetOptions options, TimeProvider time, Func<byte[]>? nonceSource = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        options.Validate();

        _http = httpClient;
        _endpoint = new Uri(options.Endpoint, UriKind.Absolute);
        _time = time;
        _nonceSource = nonceSource ?? RandomNonce;
    }

    /// <inheritdoc />
    public string TargetId => Id;

    /// <inheritdoc />
    public async Task<AuditAnchorReceipt> AnchorAsync(ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct)
    {
        ValidateDigest(checkpointDigest.Span);

        var nonce = _nonceSource();
        var request = BuildRequest(checkpointDigest.Span, nonce);
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue(_requestMediaType);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_replyMediaType));

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("The time-stamp authority returned an error status.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("The time-stamp authority response is too large.");
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (body.Length > MaxResponseBytes)
        {
            throw new InvalidDataException("The time-stamp authority response is too large.");
        }

        var token = ParseGrantedToken(body);
        var info = ReadTstInfo(token);
        if (!info.HashedMessage.AsSpan().SequenceEqual(checkpointDigest.Span))
        {
            throw new InvalidDataException("The time-stamp token does not cover the requested digest.");
        }

        var expectedNonce = new AsnReader(AsnDer.Integer(nonce)).Read().Content;
        if (info.Nonce is null || !info.Nonce.AsSpan().SequenceEqual(expectedNonce))
        {
            throw new InvalidDataException("The time-stamp token does not carry the requested nonce.");
        }

        return new AuditAnchorReceipt(TargetId, token, TruncateToMicroseconds(_time.GetUtcNow()));
    }

    /// <inheritdoc />
    public Task<bool> VerifyAsync(AuditAnchorReceipt receipt, ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateDigest(checkpointDigest.Span);

        if (receipt.TargetId != TargetId)
        {
            return Task.FromResult(false);
        }

        try
        {
            var info = ReadTstInfo(receipt.ReceiptBytes);
            return Task.FromResult(info.HashedMessage.AsSpan().SequenceEqual(checkpointDigest.Span));
        }
        catch (FormatException)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// A random nonce, positive and with a non-zero leading byte, so the request INTEGER is never zero.
    /// </summary>
    private static byte[] RandomNonce()
    {
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        nonce[0] = (byte)((nonce[0] & 0x7F) | 0x40);
        return nonce;
    }

    /// <summary>
    /// Builds a TimeStampReq (RFC 3161 section 2.4.1): version 1, the message imprint (SHA-256 and digest), the nonce, and
    /// certReq TRUE so the response carries the certificate needed for offline checks.
    /// </summary>
    internal static byte[] BuildRequest(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> nonce)
    {
        var messageImprint = AsnDer.Sequence(_sha256Algorithm, AsnDer.OctetString(digest));
        return AsnDer.Sequence(
            AsnDer.Integer([0x01]),
            messageImprint,
            AsnDer.Integer(nonce),
            AsnDer.Boolean(true));
    }

    /// <summary>
    /// Reads a TimeStampResp (section 2.4.2) and returns its TimeStampToken. Only status granted (0) and grantedWithMods (1)
    /// yield a token; any other status throws.
    /// </summary>
    private static byte[] ParseGrantedToken(byte[] response)
    {
        var resp = new AsnReader(response).Open(AsnDer.SequenceTag);
        var statusInfo = resp.Open(AsnDer.SequenceTag);
        var status = AsnReader.Expect(statusInfo.Read(), AsnDer.IntegerTag).Content;
        if (status.Length != 1 || status[0] > 1)
        {
            throw new InvalidDataException("The time-stamp authority did not grant the request.");
        }

        if (!resp.HasMore)
        {
            throw new InvalidDataException("The time-stamp response carries no token.");
        }

        return resp.Read().Encoded;
    }

    /// <summary>
    /// Walks a TimeStampToken (RFC 3161 section 2.4.2, a CMS SignedData with a TSTInfo content) to the TSTInfo fields this
    /// target checks: the message imprint and the optional nonce.
    /// </summary>
    private static TstInfo ReadTstInfo(byte[] token)
    {
        var contentInfo = new AsnReader(token).Open(AsnDer.SequenceTag);
        var signedDataOid = AsnReader.Expect(contentInfo.Read(), AsnDer.ObjectIdentifierTag);
        if (!signedDataOid.Encoded.AsSpan().SequenceEqual(AsnDer.ObjectIdentifier(_signedDataOid)))
        {
            throw new FormatException("The time-stamp token is not CMS SignedData.");
        }

        var signedData = new AsnReader(AsnReader.Expect(contentInfo.Read(), 0xA0).Content).Open(AsnDer.SequenceTag);
        signedData.Read(); // version
        signedData.Read(); // digestAlgorithms
        var encapContent = signedData.Open(AsnDer.SequenceTag);
        var eContentType = AsnReader.Expect(encapContent.Read(), AsnDer.ObjectIdentifierTag);
        if (!eContentType.Encoded.AsSpan().SequenceEqual(AsnDer.ObjectIdentifier(_tstInfoOid)))
        {
            throw new FormatException("The time-stamp token does not carry TSTInfo.");
        }

        var eContent = AsnReader.Expect(encapContent.Read(), 0xA0);
        var tstInfoOctets = AsnReader.Expect(new AsnReader(eContent.Content).Read(), AsnDer.OctetStringTag);
        var tstInfo = new AsnReader(tstInfoOctets.Content).Open(AsnDer.SequenceTag);

        tstInfo.Read(); // version
        tstInfo.Read(); // policy
        var messageImprint = tstInfo.Open(AsnDer.SequenceTag);
        var algorithm = AsnReader.Expect(messageImprint.Read(), AsnDer.SequenceTag);
        if (!algorithm.Encoded.AsSpan().SequenceEqual(_sha256Algorithm))
        {
            throw new FormatException("The time-stamp token does not use SHA-256.");
        }

        var hashed = AsnReader.Expect(messageImprint.Read(), AsnDer.OctetStringTag).Content;
        if (hashed.Length != _digestSize)
        {
            throw new FormatException("The time-stamp token digest has the wrong length.");
        }

        tstInfo.Read(); // serialNumber
        tstInfo.Read(); // genTime

        byte[]? nonce = null;
        while (tstInfo.HasMore)
        {
            var field = tstInfo.Read();
            if (field.Tag == AsnDer.IntegerTag)
            {
                nonce = field.Content;
                break;
            }
        }

        return new TstInfo(hashed, nonce);
    }

    private static void ValidateDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != _digestSize)
        {
            throw new ArgumentException($"Checkpoint digest must be {_digestSize} bytes.", nameof(digest));
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>The TSTInfo fields this target reads. <see cref="Nonce"/> is the INTEGER content, or null when absent.</summary>
    private sealed record TstInfo(byte[] HashedMessage, byte[]? Nonce);
}

/// <summary>
/// Settings for the RFC 3161 target. The endpoint is an operator setting and is never hardcoded. Credentials are not part of
/// the URL: an endpoint with user information is refused.
/// </summary>
public sealed record Rfc3161TargetOptions
{
    /// <summary>Whether this target is used when anchoring is enabled. The shipped default is on, and it still needs an endpoint.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Absolute http or https URL of the time-stamp authority. Empty until the operator sets it.</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Throws <see cref="ArgumentException"/> when the endpoint is missing, relative, not http(s) or carries credentials.</summary>
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The RFC 3161 endpoint must be an absolute http or https URL.", nameof(Endpoint));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("The RFC 3161 endpoint must not carry credentials in the URL.", nameof(Endpoint));
        }
    }
}
