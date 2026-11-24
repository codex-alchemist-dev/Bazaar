using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Bazaar.Archive.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Archive.Http.Tests;

public sealed class ArchiveRoutesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-archive-http-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient _client = new();
    private readonly List<string> _marked = [];

    private sealed class Source(List<string> marked) : IArchiveSource
    {
        public async IAsyncEnumerable<ArchiveRecord> ReadAsync(ArchiveUnit unit, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ArchiveRecord("r1", "gc_message", DateTimeOffset.UnixEpoch, "the needle is here");
            await Task.Yield();
        }

        public Task MarkArchivedAsync(ArchiveUnit unit, string rootHash, CancellationToken ct)
        {
            marked.Add(unit.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class Factory(List<string> marked) : IArchiveSourceFactory
    {
        public IArchiveSource CreateFor(Guid ownerId) => new Source(marked);
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new ArchiveOptions());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IArchiveStore>(new DirectoryArchiveStore(_root));
        builder.Services.AddSingleton<IArchiveSearchIndex, InMemoryArchiveSearchIndex>();
        builder.Services.AddSingleton<IArchiveSourceFactory>(new Factory(_marked));
        _app = builder.Build();
        _app.MapArchive((http, run) =>
        {
            var kind = http.Request.Headers["X-Kind"].ToString();
            return kind.Length == 0 ? Task.FromResult(Results.StatusCode(401)) : run(http, Guid.NewGuid(), kind);
        });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? kind = "owner")
    {
        var req = new HttpRequestMessage(method, url);
        if (kind is not null)
        {
            req.Headers.Add("X-Kind", kind);
        }

        return _client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Archive_verify_list_and_search_work_for_the_owner()
    {
        var created = await SendAsync(HttpMethod.Post, "/archive/units/gc/gc-1");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, doc.RootElement.GetProperty("records").GetInt32());
        Assert.Equal(["gc-1"], _marked);

        var verify = await SendAsync(HttpMethod.Post, "/archive/units/gc/gc-1/verify");
        Assert.Contains("\"ok\":true", await verify.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var list = await SendAsync(HttpMethod.Get, "/archive/units");
        Assert.Contains("gc-1", await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var search = await SendAsync(HttpMethod.Get, "/archive/search?q=needle");
        Assert.Contains("r1", await search.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_archive_conflicts_and_restore_without_a_target_is_503()
    {
        await SendAsync(HttpMethod.Post, "/archive/units/gc/gc-2");
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/archive/units/gc/gc-2")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(HttpMethod.Post, "/archive/units/gc/gc-2/restore")).StatusCode);
    }

    [Fact]
    public async Task Non_owners_and_unauthenticated_callers_are_refused_and_bad_input_is_400()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/archive/units/gc/gc-3", "human")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/archive/units", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/archive/units/nope/gc-3")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/archive/search?q=")).StatusCode);
        Assert.Empty(_marked);
    }
}
