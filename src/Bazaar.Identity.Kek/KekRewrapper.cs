using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Bazaar.Identity.Kek;

/// <summary>Outcome of one re-wrap pass. Succeeded means every live row now opens under the target KEK.</summary>
public sealed record KekRewrapReport(int Scanned, int Rewrapped, int AlreadyCurrent, int Conflicts, IReadOnlyList<Guid> Failed)
{
    public bool Succeeded => Failed.Count == 0 && Conflicts == 0;
}

/// <summary>
/// Moves every live wrapped key from one KEK to another without re-encrypting any row (doc 14).
/// Safety rules, all in code:
/// <list type="bullet">
/// <item>Resumable: each row is tried under the target first, so a rerun skips rows already moved.</item>
/// <item>Crash-safe: a row's wrap is replaced by one compare-and-set, after the new wrap has been opened under the target. A crash
/// leaves every row openable under exactly one KEK.</item>
/// <item>Verify before replace: the new wrap is opened under the target and checked against the plaintext first.</item>
/// <item>Nothing is deleted here. The source key file stays until an operator removes it after a successful report.</item>
/// <item>Audited: a start event and a completion event (with counts, never key material) go to <see cref="IKekAuditSink"/>.</item>
/// </list>
/// </summary>
public static class KekRewriter
{
    public const string StartedAuditKind = "identity.kek.rewrap.started";
    public const string CompletedAuditKind = "identity.kek.rewrap.completed";

    /// <summary>Re-wraps the rows of one store. See the overload that takes several stores.</summary>
    public static Task<KekRewrapReport> RunAsync(
        IKekProvider from,
        IKekProvider to,
        IKekRewrapStore store,
        IKekAuditSink audit,
        int batchSize = 200,
        CancellationToken ct = default) =>
        RunAsync(from, to, [store], audit, batchSize, ct);

    /// <summary>
    /// Re-wraps every live row of every store (GC person keys and Soul DEKs, for example) as one audited pass. The report totals
    /// all stores; row ids are Guids and never collide across tables in practice, and a failed id is still listed once.
    /// </summary>
    public static async Task<KekRewrapReport> RunAsync(
        IKekProvider from,
        IKekProvider to,
        IReadOnlyList<IKekRewrapStore> stores,
        IKekAuditSink audit,
        int batchSize = 200,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (string.Equals(from.KekId, to.KekId, StringComparison.Ordinal))
            throw new ArgumentException("The source and target KEK are the same provider; there is nothing to re-wrap.", nameof(to));

        await audit.RecordAsync(StartedAuditKind, Payload(("from", from.KekId), ("to", to.KekId)), ct).ConfigureAwait(false);

        int scanned = 0, rewrapped = 0, current = 0, conflicts = 0;
        var failed = new List<Guid>();

        foreach (var store in stores)
        {
            Guid? cursor = null;
            while (true)
            {
                var batch = await store.ListLiveWrapsAsync(cursor, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                    break;

                foreach (var row in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;
                    cursor = row.Id;

                    if (await OpensUnder(to, row, ct).ConfigureAwait(false))
                    {
                        current++;
                        continue;
                    }

                    var outcome = await MoveAsync(from, to, store, row, ct).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case RowOutcome.Rewrapped: rewrapped++; break;
                        case RowOutcome.Conflict: conflicts++; break;
                        default: failed.Add(row.Id); break;
                    }
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        var report = new KekRewrapReport(scanned, rewrapped, current, conflicts, failed);
        await audit.RecordAsync(
            CompletedAuditKind,
            Payload(
                ("from", from.KekId),
                ("to", to.KekId),
                ("scanned", report.Scanned),
                ("rewrapped", report.Rewrapped),
                ("already_current", report.AlreadyCurrent),
                ("conflicts", report.Conflicts),
                ("failed", report.Failed.Count),
                ("succeeded", report.Succeeded)),
            ct).ConfigureAwait(false);
        return report;
    }

    private enum RowOutcome
    {
        Rewrapped,
        Conflict,
        Failed,
    }

    private static async Task<RowOutcome> MoveAsync(IKekProvider from, IKekProvider to, IKekRewrapStore store, KekWrapRecord row, CancellationToken ct)
    {
        byte[]? plain = null;
        try
        {
            try
            {
                plain = await from.UnwrapAsync(row.Wrapped, row.AssociatedData, ct).ConfigureAwait(false);
            }
            catch (KekUnwrapException)
            {
                return RowOutcome.Failed; // Opens under neither KEK: left alone and reported.
            }

            var replacement = await to.WrapAsync(plain, row.AssociatedData, ct).ConfigureAwait(false);
            var check = await to.UnwrapAsync(replacement, row.AssociatedData, ct).ConfigureAwait(false);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(check, plain))
                    return RowOutcome.Failed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(check);
            }

            return await store.ReplaceWrapAsync(row.Id, row.Wrapped, replacement, to.KekId, ct).ConfigureAwait(false)
                ? RowOutcome.Rewrapped
                : RowOutcome.Conflict;
        }
        catch (KekUnwrapException)
        {
            return RowOutcome.Failed;
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static async Task<bool> OpensUnder(IKekProvider kek, KekWrapRecord row, CancellationToken ct)
    {
        try
        {
            var plain = await kek.UnwrapAsync(row.Wrapped, row.AssociatedData, ct).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch (KekUnwrapException)
        {
            return false;
        }
    }

    /// <summary>Builds the audit payload as JSON from named fields. Only ids and counts go in; never key material.</summary>
    private static string Payload(params (string Name, object Value)[] fields)
    {
        var node = new JsonObject();
        foreach (var (name, value) in fields)
            node[name] = value switch
            {
                string text => JsonValue.Create(text),
                int number => JsonValue.Create(number),
                bool flag => JsonValue.Create(flag),
                _ => throw new ArgumentException("Unsupported audit payload value.", nameof(fields)),
            };
        return node.ToJsonString();
    }
}
