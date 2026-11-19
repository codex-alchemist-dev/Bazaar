using System.Text.Json;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Cloudflare;
using Bazaar.Server.MailBackends.Common;
using Bazaar.Server.MailBackends.Generic;

namespace Bazaar.Server.MailBackends.Tests.Cloudflare;

public sealed class HttpsSenderBackendTests
{
    private sealed class Credentials : IMailCredentialSource
    {
        public Task<MailCredential> GetAsync(MailboxRef box, CancellationToken ct) =>
            Task.FromResult(new MailCredential("user", "pw"));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"r1\"}") };
        }
    }

    [Theory]
    [InlineData("\"mallory@evil.example\" <ada@example.net>", "ada@example.net")]
    [InlineData("Ada <ada@example.net>", "ada@example.net")]
    [InlineData("ada@example.net", "ada@example.net")]
    public async Task The_recipient_list_sent_to_the_api_holds_the_parsed_addresses_only(string recipient, string expected)
    {
        var handler = new CapturingHandler();
        var backend = new HttpsSenderBackend(
            "example.org", new HttpClient(handler), new Uri("https://api.example.test/"), new Credentials(), new SendLedger(), "footer");

        await backend.SendAsync(
            new OutboundMail("k1", new MailboxRef("example.org", "bot"), [recipient], "Subject", "Body"),
            TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(handler.Body!);
        var sent = json.RootElement.GetProperty("to").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal([expected], sent);
    }
}
