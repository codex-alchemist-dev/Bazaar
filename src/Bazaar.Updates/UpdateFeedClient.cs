using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Updates;

/// <summary>
/// Reads the company server's update feed and downloads the package it names (docs/design/37, section 4b, PROPOSED
/// until the owner confirms it). The feed is untrusted: this client only fetches and parses. The caller then checks
/// the package with <see cref="UpdateDecision"/> and <see cref="UpdateVerifier"/> before anything is installed.
/// </summary>
/// <remarks>
/// The client talks only to its own company server. Pass an <see cref="HttpClient"/> built on the pinned handler
/// (CompanyIdentity's <c>ServerPin.CreatePinnedHandler</c>) in production, and a fake handler in tests.
/// </remarks>
public sealed class UpdateFeedClient
{
    /// <summary>The route the company server serves, relative to its base address (section 4b).</summary>
    public const string LatestRoute = "updates/latest";

    /// <summary>The largest package the client will download. Larger offers are refused without a partial install.</summary>
    public const int MaxPackageBytes = 512 * 1024 * 1024;

    private const int _sha256HexLength = 64;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Creates a client over an <see cref="HttpClient"/> whose base address is the company server.</summary>
    public UpdateFeedClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("The HttpClient needs the company server as its BaseAddress.", nameof(http));
        }

        _http = http;
    }

    /// <summary>
    /// Fetches the latest manifest. Returns null when the server has no release for this client (404). Any other
    /// failure throws, so a broken feed is never mistaken for "no update".
    /// </summary>
    public async Task<UpdateManifest?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRoute);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<FeedDocument>(_json, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The update feed sent an empty body.");

        return Map(body);
    }

    /// <summary>
    /// Downloads the package named by <paramref name="manifest"/> into memory, up to <see cref="MaxPackageBytes"/>.
    /// The package URL must pass the same transport rule as <see cref="UpdateDecision"/> (HTTPS, or loopback HTTP).
    /// </summary>
    public async Task<byte[]> DownloadAsync(UpdateManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!UpdateDecision.IsSecureTransport(manifest.PackageUrl))
        {
            throw new InvalidOperationException("Refusing to download an update over an insecure transport.");
        }

        using var response = await _http.GetAsync(manifest.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxPackageBytes)
        {
            throw new InvalidDataException($"The update package is {declared} bytes, over the {MaxPackageBytes} byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxPackageBytes)
            {
                throw new InvalidDataException($"The update package is over the {MaxPackageBytes} byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static UpdateManifest Map(FeedDocument body)
    {
        if (string.IsNullOrWhiteSpace(body.Version))
        {
            throw new InvalidDataException("The update feed omitted the version.");
        }

        if (!Uri.TryCreate(body.PackageUrl, UriKind.Absolute, out var packageUrl))
        {
            throw new InvalidDataException("The update feed sent a package URL that is not absolute.");
        }

        var sha256 = body.Sha256 ?? string.Empty;
        if (sha256.Length != _sha256HexLength || !sha256.All(IsLowerHex))
        {
            throw new InvalidDataException("The update feed sent a SHA-256 that is not 64 lowercase hex characters.");
        }

        if (string.IsNullOrEmpty(body.Signature))
        {
            throw new InvalidDataException("The update feed omitted the signature.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(body.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The update feed sent a signature that is not base64.", ex);
        }

        return new UpdateManifest(body.Version, packageUrl, sha256, signature);
    }

    private static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    /// <summary>The wire shape of section 4b. Field names are fixed by the contract.</summary>
    private sealed class FeedDocument
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("packageUrl")]
        public string? PackageUrl { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }
    }
}
