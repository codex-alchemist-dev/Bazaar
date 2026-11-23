using System.Net;
using System.Text;
using Bazaar.Client.Souls;

namespace Bazaar.Client.Souls.Tests;

public class HttpSoulProfileClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Path, string? Auth)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            return Task.FromResult(answer(request));
        }
    }

    private static HttpSoulProfileClient Make(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://server.test/") }, () => Task.FromResult<string?>("tok"));

    [Fact]
    public async Task A_profile_is_read_with_the_bearer_and_mapped_to_the_cards_types()
    {
        var soul = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var json = $$"""{"soul_id":"{{soul}}","name":"Claude","role":"Developer","model_badge":"Haiku 5.5","versions":[{"version":1,"parent_version":null,"charter_sha256":"aa","preferences_json":"{}","content_sha256":"bb","author":"owner","reason":"first","decision_ref":null,"created_at":"2026-11-22T00:00:00+00:00"}]}""";
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        var profile = await Make(handler).GetAsync(agent, TestContext.Current.CancellationToken);

        Assert.Equal("Claude", profile!.Profile.Name);
        Assert.Equal(soul, profile.Profile.SoulId);
        Assert.Equal("Haiku 5.5", profile.Profile.ModelBadge);
        Assert.Equal(soul, profile.Versions.Single().SoulId);
        Assert.Equal("Bearer tok", handler.Seen.Single().Auth);
        Assert.Equal($"/agents/{agent}/profile", handler.Seen.Single().Path);
        // The card's own view model accepts what the client mapped.
        Assert.Equal(1, new SoulProfileViewModel(profile.Profile, profile.Versions).CurrentVersion);
    }

    [Fact]
    public async Task A_404_reads_as_null_and_a_401_throws()
    {
        var missing = Make(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await missing.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        var refused = Make(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() => refused.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
