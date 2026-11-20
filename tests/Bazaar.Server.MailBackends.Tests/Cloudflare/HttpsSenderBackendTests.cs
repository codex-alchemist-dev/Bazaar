using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Cloudflare;
using Bazaar.Server.MailBackends.Common;
using Bazaar.Server.MailBackends.Generic;

namespace Bazaar.Server.MailBackends.Tests.Cloudflare;

public sealed class HttpsSenderBackendTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"api-1\"}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Login : IMailCredentialSource
    {
        public Task<MailCredential> GetAsync(MailboxRef box, CancellationToken ct) => Task.FromResult(new MailCredential("user", "secret"));
    }

    private static async Task<string> SentTextAsync(OutboundMail mail)
    {
        var handler = new Capture();
        using var http = new HttpClient(handler);
        var backend = new HttpsSenderBackend(
            "example.org", http, new Uri("https://api.example.test/"), new Login(), new SendLedger(), "backend default footer");
        await backend.SendAsync(mail, TestContext.Current.CancellationToken);
        return JsonElement.Parse(handler.Body!).GetProperty("text").GetString()!;
    }

    private static OutboundMail Mail(string? footer) =>
        new("k-1", new MailboxRef("example.org", "ada"), ["owner@example.net"], "Hello", "Body text", Guid.NewGuid(), footer);

    [Fact]
    public async Task A_footer_set_on_the_mail_replaces_the_back_ends_default()
    {
        var text = await SentTextAsync(Mail("the policy footer"));

        Assert.Contains("the policy footer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("backend default footer", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_missing_footer_falls_back_to_the_back_ends_default_and_never_to_none(string? footer)
    {
        var text = await SentTextAsync(Mail(footer));

        Assert.Contains("backend default footer", text, StringComparison.Ordinal);
    }
}
