using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;

namespace Bazaar.Client.Org.Tests;

public class HttpOrgSourceTests
{
    private static readonly ServerEndpoint _endpoint = new(new Uri("http://localhost:5000/"), "tok");

    [Fact]
    public async Task Posts_to_the_route_with_the_bearer_token_and_reads_the_model()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"projects":[{"id":"p1","name":"P"}],"agents":[{"id":"a1","name":"A"}],"edges":[]}""");
        var source = HttpOrgSource.Create(_endpoint, handler);

        var result = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Single(result.Agents);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/company/org", handler.Path);
        Assert.Equal("Bearer tok", handler.Authorization);
    }

    [Fact]
    public async Task A_non_200_answer_throws_so_the_caller_treats_it_as_no_data()
    {
        var source = HttpOrgSource.Create(_endpoint, new StubHandler(HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(async () => await source.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_body_the_reader_rejects_throws_a_pane_model_exception()
    {
        var source = HttpOrgSource.Create(_endpoint, new StubHandler(HttpStatusCode.OK, """{"projects":1}"""));

        await Assert.ThrowsAsync<PaneModelException>(async () => await source.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")) });
        }
    }
}
