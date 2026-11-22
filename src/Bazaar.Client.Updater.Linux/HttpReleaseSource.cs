using System.Text.Json;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// Reads the manifest and its files from the company server (doc 37 section 4b: <c>GET /updates/latest</c>). The
/// <see cref="HttpClient"/> comes from the head, built on the connection that pins the server's key (section 4a). This
/// class adds the rule that nothing else is ever contacted: every URL, including after a redirect, must have the manifest
/// address's scheme, host and port. A relative URL resolves against the manifest address.
/// </summary>
public sealed class HttpReleaseSource : IReleaseSource
{
    /// <summary>Largest manifest the source reads, 1 MiB.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _manifestUri;

    /// <summary>Creates a source for the manifest at <paramref name="manifestUri"/>.</summary>
    public HttpReleaseSource(HttpClient http, Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The manifest address must be absolute.", nameof(manifestUri));
        }

        _http = http;
        _manifestUri = manifestUri;
    }

    /// <inheritdoc />
    public async Task<ReleaseManifest?> GetManifestAsync(CancellationToken ct)
    {
        using var response = await SendAsync(_manifestUri, ct).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
            {
                throw new IOException("The manifest is larger than the limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.ToArray(), WireJsonContext.Default.ReleaseManifest);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new IOException("The manifest is not valid: " + e.Message, e);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenAsync(string url, CancellationToken ct)
    {
        var uri = Resolve(url);
        var response = await SendAsync(uri, ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var final = response.RequestMessage?.RequestUri ?? uri;
        if (!SameOrigin(final))
        {
            response.Dispose();
            throw new HttpRequestException("The server redirected to another origin; the updater only talks to its own server.");
        }

        return response;
    }

    private Uri Resolve(string url)
    {
        var uri = url.Contains("://", StringComparison.Ordinal) ? new Uri(url, UriKind.Absolute) : new Uri(_manifestUri, url);
        if (!SameOrigin(uri))
        {
            throw new HttpRequestException("The manifest points at another origin; the updater only talks to its own server.");
        }

        return uri;
    }

    private bool SameOrigin(Uri uri) =>
        string.Equals(uri.Scheme, _manifestUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _manifestUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _manifestUri.Port;
}
