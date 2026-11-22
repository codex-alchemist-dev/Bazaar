using System.Net.Http.Headers;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// Reads a provider's own model list: <c>openai_chat</c> (<c>GET {base}/models</c>, bearer key), <c>anthropic_messages</c>
/// (<c>GET {base}/v1/models</c>, <c>x-api-key</c> and <c>anthropic-version</c>, paged with <c>after_id</c>) and
/// <c>gemini_generate_content</c> (<c>GET {base}/models</c>, <c>x-goog-api-key</c>, paged with <c>pageToken</c>). An unknown adapter
/// answers null. The key is resolved only to set the header and is never logged or put in a URL. A listing stops after
/// <see cref="MaxPages"/> pages. The injected <see cref="HttpClient"/> must have a timeout.
/// </summary>
public sealed class HttpProviderModelLister(
    ICatalogueStore store,
    IProviderSecretSource secrets,
    HttpClient http) : IProviderModelLister
{
    /// <summary>Upper bound on pages read for one provider, so a server that never ends its cursor cannot hold a refresh forever.</summary>
    public const int MaxPages = 20;

    private const string _openAiChatAdapter = "openai_chat";
    private const string _anthropicAdapter = "anthropic_messages";
    private const string _geminiAdapter = "gemini_generate_content";
    private const string _anthropicVersion = "2023-06-01";
    private const int _pageSize = 1000;

    public async Task<IReadOnlyList<string>?> ListModelIdsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var providers = await store.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        if (provider is null
            || (provider.Adapter != _openAiChatAdapter && provider.Adapter != _anthropicAdapter && provider.Adapter != _geminiAdapter))
        {
            return null;
        }

        string? key = null;
        if (!string.IsNullOrEmpty(provider.SecretRef))
        {
            key = await secrets.ResolveAsync(provider.SecretRef, cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                return null;
            }
        }

        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var ids = new List<string>();
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            var page = await ReadPageAsync(provider.Adapter, baseUrl, key, cursor, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return null;
            }

            ids.AddRange(page.Ids);
            if (page.NextCursor is null)
            {
                return ids;
            }

            cursor = page.NextCursor;
        }

        // The cursor never ended. A partial list would wrongly mark the rest missing, so it counts as a failed read.
        return null;
    }

    private async Task<ModelListPage?> ReadPageAsync(string adapter, string baseUrl, string? key, string? cursor, CancellationToken cancellationToken)
    {
        var uriText = adapter switch
        {
            _anthropicAdapter => $"{baseUrl}/v1/models?limit={_pageSize}" + (cursor is null ? "" : $"&after_id={Uri.EscapeDataString(cursor)}"),
            _geminiAdapter => $"{baseUrl}/models?pageSize={_pageSize}" + (cursor is null ? "" : $"&pageToken={Uri.EscapeDataString(cursor)}"),
            _ => $"{baseUrl}/models",
        };
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(key))
        {
            switch (adapter)
            {
                case _anthropicAdapter:
                    request.Headers.TryAddWithoutValidation("x-api-key", key);
                    break;
                case _geminiAdapter:
                    request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                    break;
                default:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    break;
            }
        }

        if (adapter == _anthropicAdapter)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", _anthropicVersion);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return adapter switch
            {
                _anthropicAdapter => ModelListParsers.TryParseAnthropic(body, out var a) ? a : null,
                _geminiAdapter => ModelListParsers.TryParseGemini(body, out var g) ? g : null,
                _ => OpenAiModelListParser.TryParse(body, out var ids) ? new ModelListPage(ids, null) : null,
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
