using System.Text;
using System.Text.Json;
using Cdax.Core;
using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync.Tests;

public sealed class OfflineSyncBatchHandlerTests
{
    private static readonly Guid _device = Guid.Parse("6f1d0a52-8f7e-4c3b-9a71-2d5e4b8c9f01");
    private static readonly Guid _other = Guid.Parse("0b9e4c21-3a5d-4f6e-8b7c-1d2e3f4a5b6c");

    private sealed class FakeAuth(OfflineSyncAuthResult result) : IOfflineSyncRequestAuthenticator
    {
        public ValueTask<OfflineSyncAuthResult> AuthenticateAsync(HttpContext http, CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private static (OfflineSyncBatchHandler Handler, FakeRule Rule) Build(OfflineSyncAuthResult? auth)
    {
        var rule = new FakeRule(OfflineDataClass.Preference);
        var services = new ServiceCollection();
        if (auth is not null)
        {
            services.AddSingleton<IOfflineSyncRequestAuthenticator>(new FakeAuth(auth));
        }

        var sp = services.BuildServiceProvider();
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule]);
        return (new OfflineSyncBatchHandler(sp, processor), rule);
    }

    private static async Task<(int Status, string Body)> Call(OfflineSyncBatchHandler handler, string? body, long? length = null)
    {
        var http = new DefaultHttpContext();
        http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = length ?? bytes.Length;
        var response = new MemoryStream();
        http.Response.Body = response;
        var result = await handler.HandleAsync(http);
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
    }

    private static string BatchJson(Guid batchDevice, Guid writeDevice, long sequence = 1)
    {
        var write = Make.Write(writeDevice, sequence);
        var batch = new OfflineWriteBatch(batchDevice, [write]);
        return JsonSerializer.Serialize(OfflineSyncWire.ToDto(batch), OfflineSyncWireContext.Default.OfflineBatchRequestDto);
    }

    private static OfflineSyncAuthResult Ok(Guid device) => OfflineSyncAuthResult.Success(new OfflineSyncCaller(Guid.NewGuid(), device));

    [Fact]
    public async Task Valid_batch_for_the_authenticated_device_is_applied_and_answered()
    {
        var (handler, rule) = Build(Ok(_device));

        var (status, body) = await Call(handler, BatchJson(_device, _device));

        Assert.Equal(200, status);
        var result = OfflineSyncWire.ToResult(JsonSerializer.Deserialize(body, OfflineSyncWireContext.Default.OfflineBatchResponseDto)!);
        Assert.Equal(1, result.AckedSequence);
        Assert.Equal(OfflineOutcomeKind.Applied, Assert.Single(result.Outcomes).Kind);
        Assert.Single(rule.Applied);
    }

    [Fact]
    public async Task Batch_for_another_device_is_refused_and_nothing_is_applied()
    {
        var (handler, rule) = Build(Ok(_device));

        var (status, body) = await Call(handler, BatchJson(_other, _other));

        Assert.Equal(403, status);
        Assert.Contains("device_mismatch", body, StringComparison.Ordinal);
        Assert.Empty(rule.Applied);
    }

    [Fact]
    public async Task Write_naming_a_different_device_than_the_batch_is_a_bad_request()
    {
        var (handler, rule) = Build(Ok(_device));

        var (status, _) = await Call(handler, BatchJson(_device, _other));

        Assert.Equal(400, status);
        Assert.Empty(rule.Applied);
    }

    [Fact]
    public async Task Unauthenticated_request_gets_the_authenticator_refusal()
    {
        var (handler, rule) = Build(OfflineSyncAuthResult.Refuse(401, "unauthorized"));

        var (status, _) = await Call(handler, BatchJson(_device, _device));

        Assert.Equal(401, status);
        Assert.Empty(rule.Applied);
    }

    [Fact]
    public async Task Missing_authenticator_answers_503()
    {
        var (handler, _) = Build(null);

        var (status, _) = await Call(handler, BatchJson(_device, _device));

        Assert.Equal(503, status);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"device_id\":\"6f1d0a52-8f7e-4c3b-9a71-2d5e4b8c9f01\"}")]
    public async Task Malformed_body_is_a_bad_request(string body)
    {
        var (handler, _) = Build(Ok(_device));

        var (status, _) = await Call(handler, body);

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Oversized_body_is_refused_before_reading()
    {
        var (handler, _) = Build(Ok(_device));

        var (status, _) = await Call(handler, "{}", length: OfflineSyncBatchHandler.MaxBodyBytes + 1);

        Assert.Equal(413, status);
    }

    [Fact]
    public async Task Replayed_batch_returns_duplicates()
    {
        var (handler, rule) = Build(Ok(_device));
        var json = BatchJson(_device, _device);

        await Call(handler, json);
        var (_, body) = await Call(handler, json);

        var result = JsonSerializer.Deserialize(body, OfflineSyncWireContext.Default.OfflineBatchResponseDto)!;
        Assert.Equal(OfflineOutcomeKind.Duplicate, Assert.Single(result.Outcomes).Kind);
        Assert.Single(rule.Applied);
    }

    [Fact]
    public void Component_manifest_is_valid_and_registers_the_handler()
    {
        var manifest = OfflineSyncComponent.ManifestDefinition;
        Assert.Equal("offline-sync", manifest.Id);
        Assert.Equal(["gc", "kanban", "vault", "audit"], manifest.Soft);
        Assert.Empty(manifest.DependsOn);

        var services = new ServiceCollection();
        services.AddCdaxModules(new ConfigurationBuilder().Build(), [new OfflineSyncComponent()]);
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<OfflineSyncProcessor>());
        Assert.NotNull(sp.GetService<OfflineSyncBatchHandler>());
    }

    [Fact]
    public void Component_registers_nothing_when_disabled()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Components:offline-sync:Enabled"] = "false" }).Build();
        var services = new ServiceCollection();
        services.AddCdaxModules(config, [new OfflineSyncComponent()]);
        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<OfflineSyncBatchHandler>());
    }
}
