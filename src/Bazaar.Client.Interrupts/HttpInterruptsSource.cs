using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Interrupts.Panes;

namespace Bazaar.Client.Interrupts;

/// <summary>
/// Reads the Interrupts pane model from <c>POST company/interrupts</c> on the Bazaar server (doc 24 section 5, read routes). A non-200 answer
/// throws <see cref="HttpRequestException"/>; a body the model reader rejects throws its pane model exception. The caller
/// treats any exception as no data.
/// </summary>
public sealed class HttpInterruptsSource(HttpClient http, Func<string?> credential) : IInterruptStackSource
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>Creates a source bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests; the source owns the client.</summary>
    public static HttpInterruptsSource Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpInterruptsSource(http, () => endpoint.Credential);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentInterruptStack>> GetStacksAsync(CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "company/interrupts")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var token = _credential();
        if (!string.IsNullOrEmpty(token)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return InterruptStackModelReader.Read(document.RootElement);
    }
}
