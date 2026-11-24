using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Identity.Kek.OpenBao;

/// <summary>
/// The host KEK held by an OpenBao (or Vault-compatible) transit key (doc 14, <c>identity.kek_provider = openbao</c>). The key never
/// leaves OpenBao: a data key is sent to <c>transit/encrypt</c> and comes back as a <c>vault:vN:...</c> ciphertext, which is what is
/// stored as the wrap. The key is created as a derived <c>aes256-gcm96</c> key, so the associated data travels as the transit
/// <c>context</c> and a wrap opens only for the same context (the same id binding the other providers enforce). Transit key rotation
/// is native: old ciphertext versions keep opening. A request that fails for any reason other than "this ciphertext does not open"
/// (network, token, 5xx) throws <see cref="HttpRequestException"/> and is never read as an unwrap failure, so a re-wrap stops instead
/// of mislabelling rows.
/// </summary>
public sealed class OpenBaoKekProvider : IKekProvider, IDisposable
{
    private const string _contextPrefix = "bazaar-kek-v1:";
    private const string _wrapPrefix = "vault:";

    private readonly OpenBaoOptions _options;
    private readonly Func<string> _token;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private volatile bool _keyEnsured;

    public OpenBaoKekProvider(OpenBaoOptions options, Func<string> token, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(token);
        _options = options;
        _token = token;
        _ownsHttp = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = options.Timeout;
        KekId = $"openbao-transit-v1:{options.Mount}/{options.KeyName}";
    }

    /// <inheritdoc />
    public string KekId { get; }

    /// <inheritdoc />
    public async ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        await EnsureKeyAsync(ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["plaintext"] = Convert.ToBase64String(dek.Span),
            ["context"] = Context(associatedData),
        };
        using var response = await SendAsync(HttpMethod.Post, $"encrypt/{_options.KeyName}", body, ct).ConfigureAwait(false);
        var data = await ReadDataAsync(response, ct).ConfigureAwait(false);
        var ciphertext = data["ciphertext"]?.GetValue<string>();
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(_wrapPrefix, StringComparison.Ordinal))
            throw new HttpRequestException("OpenBao transit returned no ciphertext for the data key.");
        return Encoding.UTF8.GetBytes(ciphertext);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        string ciphertext;
        try
        {
            ciphertext = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(wrappedDek.Span);
        }
        catch (ArgumentException)
        {
            throw new KekUnwrapException("The wrapped key is not an OpenBao transit ciphertext.");
        }

        if (!ciphertext.StartsWith(_wrapPrefix, StringComparison.Ordinal))
            throw new KekUnwrapException("The wrapped key was not made by an OpenBao transit key.");

        var body = new JsonObject
        {
            ["ciphertext"] = ciphertext,
            ["context"] = Context(associatedData),
        };
        using var response = await SendAsync(HttpMethod.Post, $"decrypt/{_options.KeyName}", body, ct, badRequestIsUnwrapFailure: true).ConfigureAwait(false);
        var data = await ReadDataAsync(response, ct).ConfigureAwait(false);
        var plaintext = data["plaintext"]?.GetValue<string>();
        if (plaintext is null)
            throw new HttpRequestException("OpenBao transit returned no plaintext for the wrapped key.");
        try
        {
            return Convert.FromBase64String(plaintext);
        }
        catch (FormatException ex)
        {
            throw new HttpRequestException("OpenBao transit returned an unreadable plaintext.", ex);
        }
    }

    public void Dispose()
    {
        _ensureLock.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }

    private static string Context(ReadOnlyMemory<byte> associatedData)
    {
        var prefix = Encoding.ASCII.GetBytes(_contextPrefix);
        var bytes = new byte[prefix.Length + associatedData.Length];
        prefix.CopyTo(bytes, 0);
        associatedData.Span.CopyTo(bytes.AsSpan(prefix.Length));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Creates the derived transit key on first wrap when it does not exist yet. A failure is not cached; the next call tries again.</summary>
    private async Task EnsureKeyAsync(CancellationToken ct)
    {
        if (_keyEnsured)
            return;

        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_keyEnsured)
                return;

            using (var probe = await SendAsync(HttpMethod.Get, $"keys/{_options.KeyName}", null, ct, notFoundIsOk: true).ConfigureAwait(false))
            {
                if (probe.StatusCode == HttpStatusCode.NotFound)
                {
                    var create = new JsonObject { ["type"] = "aes256-gcm96", ["derived"] = true };
                    using var created = await SendAsync(HttpMethod.Post, $"keys/{_options.KeyName}", create, ct).ConfigureAwait(false);
                }
                else
                {
                    var data = await ReadDataAsync(probe, ct).ConfigureAwait(false);
                    if (data["derived"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException(
                            $"The OpenBao transit key '{_options.KeyName}' exists but is not a derived key, so it cannot bind a wrap to its data. Create it with derived=true.");
                }
            }

            _keyEnsured = true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken ct,
        bool badRequestIsUnwrapFailure = false,
        bool notFoundIsOk = false)
    {
        var uri = new Uri(_options.Address, $"/v1/{_options.Mount}/{path}");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Vault-Token", _token());
        if (_options.Namespace is not null)
            request.Headers.Add("X-Vault-Namespace", _options.Namespace);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || (notFoundIsOk && response.StatusCode == HttpStatusCode.NotFound))
            return response;

        var status = response.StatusCode;
        response.Dispose();
        if (badRequestIsUnwrapFailure && status == HttpStatusCode.BadRequest)
            throw new KekUnwrapException("OpenBao transit refused the wrapped key: it does not open for this data key.");

        // The status only. A response body can echo request values, so it is never copied into the exception.
        throw new HttpRequestException($"OpenBao transit {method} {path} failed with HTTP {(int)status}.", inner: null, status);
    }

    private static async Task<JsonNode> ReadDataAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        JsonNode? root;
        try
        {
            root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("OpenBao transit returned a response that is not JSON.", ex);
        }

        return root?["data"] ?? throw new HttpRequestException("OpenBao transit returned a response with no data.");
    }
}
