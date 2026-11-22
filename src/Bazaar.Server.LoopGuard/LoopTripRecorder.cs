using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.LoopGuard.Abstractions;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.LoopGuard;

/// <summary>
/// Handles a loop-guard trip (docs/design/06 "Loop guards"): writes the <c>guard_event</c> row, appends one system notice to the
/// GC, then admits the agents to the Psych Ward (C34). Each step runs even if an earlier one failed, so a broken notice never
/// hides the audit row; failures are logged, and the caller still refuses the post.
/// </summary>
/// <remarks>
/// The system notice is idempotent per (GC, guard kind, agent set, minute): a retry of the same refused post inside the minute
/// returns the original notice instead of writing a second one, and a trip that repeats later writes a new one.
/// </remarks>
public sealed partial class LoopTripRecorder(
    NpgsqlDataSource dataSource,
    IGcLogWriter writer,
    IPsychWardGate psychWard,
    ILogger<LoopTripRecorder> logger,
    TimeProvider? time = null) : ILoopTripHandler
{
    /// <summary>The system author id for notices written by this handler.</summary>
    public static readonly Guid SystemAuthorId = Guid.Parse("00000000-0000-7000-8000-00000000106d");

    /// <summary>The <c>action</c> in the notice payload.</summary>
    public const string NoticeAction = "loop_guard_tripped";

    private const string _insertSql = "INSERT INTO guard_event (gc_id, kind, counters) VALUES ($1, $2, $3::jsonb)";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task HandleAsync(LoopTrip trip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentException.ThrowIfNullOrWhiteSpace(trip.Kind);

        await StepAsync("guard_event", () => WriteGuardEventAsync(trip, cancellationToken), cancellationToken).ConfigureAwait(false);
        await StepAsync("notice", () => WriteNoticeAsync(trip, cancellationToken), cancellationToken).ConfigureAwait(false);
        await StepAsync("psych_ward", () => psychWard.AdmitAsync(trip, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Loop trip step {Step} failed")]
    private static partial void LogStepFailed(ILogger logger, Exception exception, string step);

    private async Task StepAsync(string step, Func<Task> run, CancellationToken cancellationToken)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStepFailed(logger, ex, step);
        }
    }

    private async Task WriteGuardEventAsync(LoopTrip trip, CancellationToken ct)
    {
        var counters = new JsonObject
        {
            ["reason"] = trip.Reason,
            ["agents"] = new JsonArray([.. trip.AgentIds.Select(a => (JsonNode?)JsonValue.Create(a.ToString("D")))]),
        };
        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = trip.GcId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = trip.Kind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = counters.ToJsonString() });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteNoticeAsync(LoopTrip trip, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["action"] = NoticeAction,
            ["kind"] = trip.Kind,
            ["reason"] = trip.Reason,
            ["agents"] = new JsonArray([.. trip.AgentIds.Select(a => (JsonNode?)JsonValue.Create(a.ToString("D")))]),
        };
        var request = new AppendRequest(
            trip.GcId,
            GcEventKinds.System,
            AuthorKind.System,
            SystemAuthorId,
            SourceTrust.System,
            NoticeKey(trip, _time.GetUtcNow()))
        {
            PayloadJson = payload.ToJsonString(),
        };
        await writer.AppendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>The idempotency key for one trip's notice: GC, kind, agent set and the minute.</summary>
    public static string NoticeKey(LoopTrip trip, DateTimeOffset now)
    {
        var agents = string.Join(',', trip.AgentIds.Select(a => a.ToString("D")).Order(StringComparer.Ordinal));
        var minute = now.ToUnixTimeSeconds() / 60;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{trip.Kind}|{agents}|{minute}"))).ToLowerInvariant();
        return $"guard:{hash}";
    }
}
