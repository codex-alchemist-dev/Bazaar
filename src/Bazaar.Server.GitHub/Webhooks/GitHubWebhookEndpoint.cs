using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub.Webhooks;

/// <summary>
/// Webhook ingress (doc 21, Ingress): verify the HMAC-SHA256 signature over the raw body in constant time, persist the delivery
/// to <c>github_delivery</c> with <c>on conflict do nothing</c>, and acknowledge. Nothing is processed inline; the poller and
/// event mapper read the table. The route is not behind the bearer guard: the signature is the credential. It fails closed:
/// with no secret or no database configured every delivery gets 503.
/// </summary>
public static partial class GitHubWebhookEndpoint
{
    /// <summary>The <c>github_delivery.source</c> for webhook deliveries; doc 21's dedupe key is <c>github:&lt;delivery&gt;</c>.</summary>
    public const string Source = "github";

    private const int _maxDeliveryIdLength = 256;
    private const int _maxEventLength = 128;

    /// <summary>Maps <c>POST /github/webhook</c> and <c>POST /hooks/github/{opaque}</c>. Called from <c>MapBazaarGitHubRoutes</c>.</summary>
    public static IEndpointRouteBuilder MapBazaarGitHubWebhook(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/github/webhook", (Func<HttpContext, Task<IResult>>)(http => HandleAsync(http, null)));
        routes.MapPost("/hooks/github/{opaque}", (HttpContext http, string opaque) => HandleAsync(http, opaque));
        return routes;
    }

    private static async Task<IResult> HandleAsync(HttpContext http, string? opaque)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<GitHubWebhookOptions>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Bazaar.GitHub.Webhooks");

        if (opaque is not null && !OpaqueMatches(options.OpaqueSegment, opaque))
            return Status(StatusCodes.Status404NotFound);

        var secret = await ResolveSecretAsync(services, options, log, http.RequestAborted);
        var store = services.GetService<IGitHubDeliveryStore>()
            ?? (services.GetService<NpgsqlDataSource>() is { } data ? new PgGitHubDeliveryStore(data) : null);
        if (secret is null || store is null)
            return Status(StatusCodes.Status503ServiceUnavailable);

        var max = Math.Clamp(options.MaxBodyBytes, 1, GitHubWebhookOptions.GitHubMaxBodyBytes);
        if (http.Request.ContentLength > max)
            return Status(StatusCodes.Status413PayloadTooLarge);

        // Read at most one byte past the cap, so a chunked body without a Content-Length is bounded too.
        var body = await ReadBoundedAsync(http.Request.Body, max, http.RequestAborted);
        if (body is null)
            return Status(StatusCodes.Status413PayloadTooLarge);

        var signature = http.Request.Headers["X-Hub-Signature-256"].ToString();
        if (!WebhookSignature.Verify(secret, body, signature.Length == 0 ? null : signature))
            return Status(StatusCodes.Status401Unauthorized);

        var deliveryId = http.Request.Headers["X-GitHub-Delivery"].ToString();
        var eventName = http.Request.Headers["X-GitHub-Event"].ToString();
        if (deliveryId.Length is 0 or > _maxDeliveryIdLength || eventName.Length is 0 or > _maxEventLength)
            return Status(StatusCodes.Status400BadRequest);

        try
        {
            var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var isNew = await store.TryRecordAsync(Source, deliveryId, eventName, time.GetUtcNow(), AsJson(body), http.RequestAborted);
            return Status(isNew ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Not recorded, so do not acknowledge: a non-2xx lets GitHub redeliver.
            LogPersistFailed(log, ex, deliveryId);
            return Status(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>The body as text when it is valid JSON (GitHub can also send form-encoded bodies), otherwise null.</summary>
    private static string? AsJson(byte[] body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
            return Encoding.UTF8.GetString(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
                return null;
        }

        return buffer.ToArray();
    }

    private static bool OpaqueMatches(string? configured, string presented)
    {
        if (string.IsNullOrEmpty(configured))
            return false;
        // Hash both so the comparison is constant time whatever the lengths.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(configured)), SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }

    private static async Task<byte[]?> ResolveSecretAsync(IServiceProvider services, GitHubWebhookOptions options, ILogger log, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SecretReference))
            return null;
        var resolver = services.GetService<ISecretResolver>();
        if (resolver is null || !resolver.CanResolve(options.SecretReference))
            return null;
        try
        {
            var value = await resolver.ResolveAsync(options.SecretReference, cancellationToken);
            return string.IsNullOrEmpty(value) ? null : Encoding.UTF8.GetBytes(value);
        }
        catch (SecretResolutionException ex)
        {
            LogSecretFailed(log, ex);
            return null;
        }
    }

    private static IResult Status(int status) => Results.StatusCode(status);

    [LoggerMessage(Level = LogLevel.Error, Message = "GitHub webhook delivery {DeliveryId} could not be persisted.")]
    private static partial void LogPersistFailed(ILogger logger, Exception exception, string deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The GitHub webhook secret could not be resolved.")]
    private static partial void LogSecretFailed(ILogger logger, Exception exception);
}
