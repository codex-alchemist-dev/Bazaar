using System.Net.Http.Headers;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// Reads <c>GET {base_url}/models</c> for providers on the <c>openai_chat</c> adapter (the default family). Other adapters have
/// no list reader here and answer null. The provider's secret is resolved only to set the bearer header and is never logged.
/// The injected <see cref="HttpClient"/> must have its timeout set.
/// </summary>
public sealed class HttpProviderModelLister(
    ICatalogueStore store,
    IProviderSecretSource secrets,
    HttpClient http) : IProviderModelLister
{
    private const string _openAiChatAdapter = "openai_chat";

    public async Task<IReadOnlyList<string>?> ListModelIdsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var providers = await store.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        if (provider is null || !string.Equals(provider.Adapter, _openAiChatAdapter, StringComparison.Ordinal))
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

        if (!Uri.TryCreate(provider.BaseUrl.TrimEnd('/') + "/models", UriKind.Absolute, out var uri))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return OpenAiModelListParser.TryParse(body, out var ids) ? ids : null;
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
