using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Common;
using Bazaar.Server.MailBackends.Generic;
using MimeKit;

namespace Bazaar.Server.MailBackends.Cloudflare;

/// <summary>
/// Outbound through an HTTPS sending API (Cloudflare Email Routing pairs with one; Resend Free is the reference).
/// Sends an Idempotency-Key header so a retry cannot send twice. Ingress is the queue receiver, not this class.
/// </summary>
public sealed class HttpsSenderBackend(
    string domain,
    HttpClient http,
    Uri apiBase,
    IMailCredentialSource credentials,
    SendLedger ledger,
    string footer) : IMailBackend
{
    private sealed record ApiSendBody(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("message_id")] string MessageId);

    private sealed record ApiSendReply([property: JsonPropertyName("id")] string? Id);

    public Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct) =>
        // Pull happens in SignedQueueReceiver, driven by the host. Nothing to fetch from the sending API.
        Task.FromResult<IReadOnlyList<RawMail>>([]);

    public async Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (ledger.Find(mail.Key) is { } prior) return prior;
        if (!string.Equals(mail.From.Domain, domain, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sender domain does not match this back end.");
        }

        var message = OutboundMessageBuilder.Build(mail, footer);
        var messageId = OutboundMessageBuilder.MessageIdFor(mail.Key, domain);
        var text = message.Body is MimeKit.TextPart plain ? plain.Text : string.Empty;
        var body = new ApiSendBody(mail.From.Address, mail.To, mail.Subject, text, messageId);

        var login = await credentials.GetAsync(mail.From, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBase, "emails"))
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Password);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", mail.Key);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ApiSendReply>(cancellationToken: ct);

        return ledger.Record(new SendReceipt(mail.Key, reply?.Id ?? messageId, AlreadySent: false));
    }

    public async Task<BackendHealth> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(apiBase, ct);
            return new BackendHealth(response.IsSuccessStatusCode || (int)response.StatusCode < 500, response.StatusCode.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BackendHealth(false, ex.GetType().Name);
        }
    }
}
