using System.Net;
using System.Text;

namespace Bazaar.Client.Walkthrough.Tests;

public class HttpWalkthroughServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (HttpWalkthroughService Service, Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        return (new HttpWalkthroughService(new HttpClient(handler) { BaseAddress = new Uri("https://server.test/") }, () => Task.FromResult<string?>("tok")), handler);
    }

    [Fact]
    public async Task Facts_are_read_with_the_bearer_and_mapped()
    {
        var (service, handler) = Make(_ => Json(HttpStatusCode.OK, """{"company_created":true,"first_agent_name":"Claude","first_task_given":false}"""));

        var facts = await service.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new WalkthroughFacts(true, "Claude", false), facts);
        Assert.Equal("Bearer tok", handler.Seen.Single().Auth);
        Assert.Equal("/onboarding/walkthrough/facts", handler.Seen.Single().Path);
    }

    [Fact]
    public async Task A_refused_facts_read_throws_so_the_view_model_keeps_what_it_knew()
    {
        var (service, _) = Make(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Progress_saves_as_a_put_and_loads_back_ignoring_unknown_steps()
    {
        var (service, handler) = Make(request => request.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Json(HttpStatusCode.OK, """{"dismissed":true,"finished":false,"current":"MeetAgent","completed":["CreateCompany","Bogus"]}"""));

        await service.SaveAsync(new WalkthroughProgress(false, true, WalkthroughStep.Done, [WalkthroughStep.CreateCompany]), TestContext.Current.CancellationToken);
        var put = handler.Seen.Single();
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Contains("\"current\":\"Done\"", put.Body);

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(loaded!.Dismissed);
        Assert.Equal(WalkthroughStep.MeetAgent, loaded.Current);
        Assert.Equal([WalkthroughStep.CreateCompany], loaded.Completed);
    }

    [Fact]
    public async Task No_saved_progress_reads_as_null()
    {
        var (service, _) = Make(_ => Json(HttpStatusCode.NotFound, "{}"));
        Assert.Null(await service.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_cached_store_falls_back_to_the_cache_when_the_server_is_down_and_still_saves_locally()
    {
        var down = new HttpWalkthroughService(new HttpClient(new Handler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("https://server.test/") }, () => Task.FromResult<string?>(null));
        var cache = new FakeStore(new WalkthroughProgress(false, false, WalkthroughStep.FirstTask, []));
        var store = new CachedWalkthroughProgressStore(down, cache);

        Assert.Equal(WalkthroughStep.FirstTask, (await store.LoadAsync(TestContext.Current.CancellationToken))!.Current);

        await store.SaveAsync(new WalkthroughProgress(true, false, WalkthroughStep.Welcome, []), TestContext.Current.CancellationToken);
        Assert.True(cache.Saved!.Dismissed);
    }
}
