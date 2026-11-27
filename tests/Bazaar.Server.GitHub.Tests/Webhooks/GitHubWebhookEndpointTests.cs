using System.Net;
using System.Reflection;
using System.Text;
using Bazaar.Server.GitHub.Webhooks;
using Bazaar.Server.Secrets;
using Bazaar.Testing.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.GitHub.Tests.Webhooks;

/// <summary><c>POST /github/webhook</c> (doc 21, Ingress): verify, dedupe, persist, acknowledge.</summary>
public sealed class GitHubWebhookEndpointTests
{
    private static readonly string[] _migrationFiles = ["0001_github_tables.sql", "0002_github_delivery_payload.sql"];
    private const string _secret = "s3cret-value";
    private const string _payload = """{"zen":"Keep it logically awesome.","hook_id":1}""";

    private sealed class FixedResolver(string? value) : ISecretResolver
    {
        public bool CanResolve(string reference) => reference.StartsWith("env:", StringComparison.Ordinal);

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            value is null ? throw new SecretResolutionException(reference, "unset") : ValueTask.FromResult(value);
    }

    private sealed class MemoryStore : IGitHubDeliveryStore
    {
        public List<(string Source, string Id, string Event)> Rows { get; } = [];

        public bool Fail { get; set; }

        public List<string?> Payloads { get; } = [];

        public Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, string? payloadJson, CancellationToken cancellationToken)
        {
            Payloads.Add(payloadJson);
            return TryRecordAsync(source, deliveryId, eventName, receivedAt, cancellationToken);
        }

        public Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("down");
            if (Rows.Any(r => r.Source == source && r.Id == deliveryId))
                return Task.FromResult(false);
            Rows.Add((source, deliveryId, eventName));
            return Task.FromResult(true);
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        IGitHubDeliveryStore? store,
        string? secretReference = "env:HOOK",
        string? secretValue = _secret,
        int maxBody = GitHubWebhookOptions.GitHubMaxBodyBytes,
        string? opaque = null,
        NpgsqlDataSource? data = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new GitHubWebhookOptions { SecretReference = secretReference, MaxBodyBytes = maxBody, OpaqueSegment = opaque });
        builder.Services.AddSingleton<ISecretResolver>(new FixedResolver(secretValue));
        if (store is not null)
            builder.Services.AddSingleton(store);
        if (data is not null)
            builder.Services.AddSingleton(data);
        var app = builder.Build();
        app.MapBazaarGitHubWebhook();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        byte[] body,
        string? signature,
        string? delivery = "d-1",
        string? eventName = "push",
        string path = "/github/webhook")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        if (signature is not null)
            request.Headers.Add("X-Hub-Signature-256", signature);
        if (delivery is not null)
            request.Headers.Add("X-GitHub-Delivery", delivery);
        if (eventName is not null)
            request.Headers.Add("X-GitHub-Event", eventName);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Sign(byte[] body, string secret = _secret) => WebhookSignature.Compute(Encoding.UTF8.GetBytes(secret), body);

    [Fact]
    public async Task A_valid_signed_delivery_is_persisted_and_acknowledged()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var response = await PostAsync(client, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([("github", "d-1", "push")], store.Rows);
    }

    [Fact]
    public async Task A_bad_signature_is_refused_and_nothing_is_stored()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var response = await PostAsync(client, body, Sign(body, "another-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_tampered_body_is_refused()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var response = await PostAsync(client, Encoding.UTF8.GetBytes(_payload + " "), Sign(body));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_missing_signature_is_refused()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;

        var response = await PostAsync(client, Encoding.UTF8.GetBytes(_payload), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_replayed_delivery_is_acknowledged_without_a_second_row()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var first = await PostAsync(client, body, Sign(body));
        var second = await PostAsync(client, body, Sign(body));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task An_oversize_body_is_refused_with_413_by_content_length()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store, maxBody: 64);
        await using var _ = app;
        var body = new byte[65];

        var response = await PostAsync(client, body, Sign(body));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_chunked_oversize_body_without_a_length_is_refused_with_413()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store, maxBody: 64);
        await using var _ = app;
        var body = new byte[65];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/github/webhook") { Content = new StreamContent(new MemoryStream(body)) };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add("X-Hub-Signature-256", Sign(body));
        request.Headers.Add("X-GitHub-Delivery", "d-1");
        request.Headers.Add("X-GitHub-Event", "push");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Theory]
    [InlineData(null, "env:HOOK")]
    [InlineData("", "env:HOOK")]
    [InlineData(_secret, null)]
    [InlineData(_secret, "")]
    public async Task With_no_secret_configured_every_delivery_gets_503_even_when_signed(string? value, string? reference)
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store, secretReference: reference, secretValue: value);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var signed = await PostAsync(client, body, Sign(body));
        var unsigned = await PostAsync(client, body, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, signed.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unsigned.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task With_no_delivery_store_or_database_the_route_answers_503()
    {
        var (app, client) = await StartAsync(null);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var response = await PostAsync(client, body, Sign(body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_store_failure_is_not_acknowledged_so_GitHub_redelivers()
    {
        var store = new MemoryStore { Fail = true };
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var response = await PostAsync(client, body, Sign(body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "push")]
    [InlineData("d-1", null)]
    public async Task A_signed_delivery_without_its_id_or_event_header_is_a_bad_request(string? delivery, string? eventName)
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var response = await PostAsync(client, body, Sign(body), delivery, eventName);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Other_methods_are_refused()
    {
        var (app, client) = await StartAsync(new MemoryStore());
        await using var _ = app;

        var response = await client.GetAsync("/github/webhook", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task The_opaque_route_needs_the_configured_segment_and_is_off_by_default()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store, opaque: "abc123");
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var wrong = await PostAsync(client, body, Sign(body), path: "/hooks/github/nope");
        var right = await PostAsync(client, body, Sign(body), path: "/hooks/github/abc123");
        var (offApp, offClient) = await StartAsync(store);
        await using var __ = offApp;
        var off = await PostAsync(offClient, body, Sign(body), "d-2", path: "/hooks/github/abc123");

        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, right.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Single(store.Rows);
    }

    private static async Task<PgTestDatabase> CreateDatabaseAsync(CancellationToken ct) =>
        await PgTestDatabase.CreateAsync("ghwebhook", async (data, token) =>
        {
            var assembly = typeof(GitHubComponent).Assembly;
            foreach (var file in _migrationFiles)
            {
                var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                await using var command = data.CreateCommand(await reader.ReadToEndAsync(token));
                await command.ExecuteNonQueryAsync(token);
            }
        }, ct);

    [Fact]
    public async Task Postgres_store_records_once_and_reports_the_replay()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        var store = new PgGitHubDeliveryStore(db.DataSource);

        var first = await store.TryRecordAsync("github", "d-1", "push", DateTimeOffset.UtcNow, ct);
        var replay = await store.TryRecordAsync("github", "d-1", "push", DateTimeOffset.UtcNow, ct);
        var otherSource = await store.TryRecordAsync("poll", "d-1", "push", DateTimeOffset.UtcNow, ct);

        Assert.True(first);
        Assert.False(replay);
        Assert.True(otherSource);
        await using var count = db.DataSource.CreateCommand("SELECT count(*) FROM github_delivery");
        Assert.Equal(2L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Endpoint_over_Postgres_persists_once_and_acknowledges_the_replay()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        var (app, client) = await StartAsync(null, data: db.DataSource);
        await using var _ = app;
        var body = Encoding.UTF8.GetBytes(_payload);

        var first = await PostAsync(client, body, Sign(body), "pg-1", "pull_request");
        var replay = await PostAsync(client, body, Sign(body), "pg-1", "pull_request");
        var bad = await PostAsync(client, body, "sha256=" + new string('0', 64), "pg-2");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        await using var rows = db.DataSource.CreateCommand("SELECT source, delivery_id, event, processed_at IS NULL FROM github_delivery");
        await using var reader = await rows.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("github", reader.GetString(0));
        Assert.Equal("pg-1", reader.GetString(1));
        Assert.Equal("pull_request", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task Postgres_store_keeps_payload_and_event_type_and_the_first_payload_on_replay()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        var store = new PgGitHubDeliveryStore(db.DataSource);

        Assert.True(await store.TryRecordAsync("github", "p-1", "push", DateTimeOffset.UtcNow, """{"a":1}""", ct));
        Assert.False(await store.TryRecordAsync("github", "p-1", "push", DateTimeOffset.UtcNow, """{"a":2}""", ct));
        Assert.True(await store.TryRecordAsync("github", "p-2", "ping", DateTimeOffset.UtcNow, ct));
        Assert.True(await store.TryRecordAsync("github", "p-3", "push", DateTimeOffset.UtcNow, "{\"t\":\"a\\u0000b\"}", ct));

        await using var rows = db.DataSource.CreateCommand("SELECT delivery_id, payload->>'a', event_type, payload IS NULL FROM github_delivery ORDER BY delivery_id");
        await using var reader = await rows.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("1", reader.GetString(1));
        Assert.Equal("push", reader.GetString(2));
        Assert.True(await reader.ReadAsync(ct));
        Assert.True(reader.GetBoolean(3));
        Assert.Equal("ping", reader.GetString(2));
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("p-3", reader.GetString(0));
        Assert.True(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Endpoint_passes_a_JSON_body_through_and_drops_a_non_JSON_body()
    {
        var store = new MemoryStore();
        var (app, client) = await StartAsync(store);
        await using var _ = app;
        var json = Encoding.UTF8.GetBytes(_payload);
        var form = Encoding.UTF8.GetBytes("payload=%7B%7D");

        await PostAsync(client, json, Sign(json), "j-1", "push");
        await PostAsync(client, form, Sign(form), "j-2", "push");

        Assert.Equal([_payload, null], store.Payloads);
    }
}
