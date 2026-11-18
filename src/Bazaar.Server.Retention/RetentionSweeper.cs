using System.Text.Json.Nodes;
using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Retention;

/// <summary>
/// Runs the retention sweep over every registered <see cref="IRetentionCategory"/> (doc 48 section 9). Per category: indefinite
/// period means skip; any active non-person hold, or a person hold on a covered person, blocks the whole category; a hold reader
/// error sweeps nothing (fail closed). Each run is bounded by the category batch size and <see cref="MaxBatchesPerRun"/>, and a
/// run with nothing to purge writes no audit row, so repeated runs are idempotent. It never touches the audit chain.
/// </summary>
public sealed class RetentionSweeper(
    IEnumerable<IRetentionCategory> categories,
    ILegalHoldReader personHolds,
    ILegalHoldScopeReader scopeHolds,
    IAuditLog audit,
    TimeProvider time)
{
    /// <summary>Upper bound on batches in one run, so a long backlog is worked off over several runs.</summary>
    public const int MaxBatchesPerRun = 20;

    private readonly IRetentionCategory[] _categories = categories.ToArray();

    /// <summary>Sweeps every category. A failing category does not stop the others.</summary>
    public async Task<IReadOnlyList<RetentionSweepResult>> SweepAsync(CancellationToken ct)
    {
        var results = new List<RetentionSweepResult>(_categories.Length);
        foreach (var category in _categories)
            results.Add(await SweepCategoryAsync(category, ct).ConfigureAwait(false));
        return results;
    }

    private async Task<RetentionSweepResult> SweepCategoryAsync(IRetentionCategory category, CancellationToken ct)
    {
        var period = await category.GetPeriodAsync(ct).ConfigureAwait(false);
        if (period is null)
            return new RetentionSweepResult(category.Name, RetentionSweepOutcome.Indefinite, 0);

        var now = time.GetUtcNow();
        var cutoff = now - period.Value;

        string? hold = null;
        try
        {
            if (await scopeHolds.HasActiveNonPersonHoldAsync(ct).ConfigureAwait(false))
                hold = "non_person";
            else
            {
                foreach (var person in await category.GetCoveredPersonsAsync(ct).ConfigureAwait(false))
                {
                    if (await personHolds.IsPersonHeldAsync(person, ct).ConfigureAwait(false))
                    {
                        hold = "person";
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AppendAsync(now, RetentionAuditKinds.FailedClosed, category.Name, cutoff, ("hold", "unknown"), ("error", ex.GetType().Name)).ConfigureAwait(false);
            return new RetentionSweepResult(category.Name, RetentionSweepOutcome.FailedClosed, 0);
        }

        if (hold is not null)
        {
            await AppendAsync(now, RetentionAuditKinds.Blocked, category.Name, cutoff, ("hold", hold)).ConfigureAwait(false);
            return new RetentionSweepResult(category.Name, RetentionSweepOutcome.Blocked, 0);
        }

        var batchSize = await category.GetBatchSizeAsync(ct).ConfigureAwait(false);
        if (batchSize < 1)
            throw new InvalidOperationException($"Retention category '{category.Name}' returned a batch size below one.");

        long deleted = 0;
        var batches = 0;
        while (batches < MaxBatchesPerRun)
        {
            var n = await category.PurgeBatchAsync(cutoff, batchSize, ct).ConfigureAwait(false);
            batches++;
            deleted += n;
            if (n < batchSize)
                break;
        }

        if (deleted > 0)
            await AppendAsync(now, RetentionAuditKinds.Swept, category.Name, cutoff, ("hold", "none"), ("deleted", deleted), ("batches", batches)).ConfigureAwait(false);
        return new RetentionSweepResult(category.Name, RetentionSweepOutcome.Swept, deleted);
    }

    private Task AppendAsync(DateTimeOffset now, string kind, string category, DateTimeOffset cutoff, params (string Key, object Value)[] extra)
    {
        var payload = new JsonObject
        {
            ["category"] = category,
            ["cutoff"] = cutoff.ToString("O"),
        };
        foreach (var (key, value) in extra)
            payload[key] = JsonValue.Create(value);
        return audit.AppendAsync(new AuditEntry(now, RetentionAuditKinds.Actor, kind, payload.ToJsonString()));
    }
}

/// <summary>What happened to one category in one run.</summary>
public enum RetentionSweepOutcome
{
    /// <summary>Period is indefinite; nothing was done.</summary>
    Indefinite,

    /// <summary>The sweep ran (it may have removed zero rows).</summary>
    Swept,

    /// <summary>A legal hold blocked the sweep.</summary>
    Blocked,

    /// <summary>The hold check failed, so nothing was swept.</summary>
    FailedClosed,
}

/// <summary>The result for one category in one run.</summary>
public sealed record RetentionSweepResult(string Category, RetentionSweepOutcome Outcome, long Deleted);
