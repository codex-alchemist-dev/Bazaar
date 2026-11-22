using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Security.Tests.Audit.Anchors;

/// <summary>
/// The receipt and the recovered event commit together (design doc 48, 5.3). A fake transactional pair stages both writes and applies
/// them on commit, so a failing audit write must leave no receipt, and the in-memory path (no writer) stays two separate writes.
/// </summary>
public sealed class AuditAnchorAtomicTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditAnchorService Service(Target target, Pair pair, bool atomic) =>
        new(
            [target],
            pair,
            pair,
            pair,
            new FixedTime(),
            TimeSpan.FromSeconds(30),
            atomic ? pair : null);

    [Fact]
    public async Task A_failing_audit_write_rolls_back_the_receipt_and_the_retry_writes_both()
    {
        var stored = AnchorFixtures.Stored();
        var target = new Target("rfc3161") { Fail = true };
        var pair = new Pair();
        var service = Service(target, pair, atomic: true);

        await service.AnchorAsync(stored, Ct);
        Assert.Equal([AuditAnchorService.FailedKind], pair.Kinds);

        // Recovery: the target works again, but the recovered event cannot be written, so the whole transaction rolls back.
        target.Fail = false;
        pair.FailAuditKind = AuditAnchorService.RecoveredKind;
        var outcome = await service.AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Failed);
        Assert.Empty(pair.Receipts);
        Assert.DoesNotContain(AuditAnchorService.RecoveredKind, pair.Kinds);
        Assert.Equal(1, pair.RollbackCount);

        // The retry sees no receipt, anchors again, and writes the receipt and the recovered event in one commit.
        pair.FailAuditKind = null;
        outcome = await service.AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Single(pair.Receipts);
        Assert.Equal(AuditAnchorService.RecoveredKind, pair.Kinds[^1]);
        Assert.Equal(1, pair.Kinds.Count(k => k == AuditAnchorService.RecoveredKind));
    }

    [Fact]
    public async Task A_failure_recorded_before_a_restart_is_found_inside_the_transaction()
    {
        var stored = AnchorFixtures.Stored();
        var target = new Target("rfc3161") { Fail = true };
        var pair = new Pair();
        await Service(target, pair, atomic: true).AnchorAsync(stored, Ct);

        // A new service instance has no cached failure; the check inside the transaction reads it from the log.
        target.Fail = false;
        var outcome = await Service(target, pair, atomic: true).AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Equal([AuditAnchorService.FailedKind, AuditAnchorService.RecoveredKind], pair.Kinds);
        Assert.Equal(1, pair.InTransactionFailureChecks);
        Assert.Equal(0, pair.OutsideFailureChecks);
        var payload = JsonNode.Parse(pair.Entries[1].Payload)!;
        Assert.Equal(stored.Checkpoint.LastSeq, payload["last_seq"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_first_time_success_writes_a_receipt_and_no_event()
    {
        var pair = new Pair();

        var outcome = await Service(new Target("rfc3161"), pair, atomic: true).AnchorAsync(AnchorFixtures.Stored(), Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Single(pair.Receipts);
        Assert.Empty(pair.Entries);
    }

    [Fact]
    public async Task The_in_memory_path_without_a_writer_still_writes_the_receipt_and_the_event_separately()
    {
        var stored = AnchorFixtures.Stored();
        var target = new Target("rfc3161") { Fail = true };
        var pair = new Pair();
        var service = Service(target, pair, atomic: false);
        await service.AnchorAsync(stored, Ct);

        target.Fail = false;
        var outcome = await service.AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Single(pair.Receipts);
        Assert.Equal([AuditAnchorService.FailedKind, AuditAnchorService.RecoveredKind], pair.Kinds);
        Assert.Equal(0, pair.InTransactionFailureChecks);
        Assert.Equal(0, pair.BeginCount);
    }

    private sealed class Target(string id) : IAuditAnchorTarget
    {
        public string TargetId => id;

        public bool Fail { get; set; }

        public Task<AuditAnchorReceipt> AnchorAsync(ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct) =>
            Fail
                ? throw new InvalidOperationException("down")
                : Task.FromResult(new AuditAnchorReceipt(id, checkpointDigest.ToArray(), AnchorFixtures.Now));

        public Task<bool> VerifyAsync(AuditAnchorReceipt receipt, ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => AnchorFixtures.Now;
    }

    /// <summary>
    /// A fake transactional pair: one store, one audit log, one failure log and the writer over them. Scope writes are staged and
    /// applied on commit; a scope disposed without commit discards them, as a rolled-back transaction does.
    /// </summary>
    private sealed class Pair : IAuditAnchorStore, IAuditLog, IAuditAnchorFailureLog, IAuditAnchorAtomicWriter
    {
        private readonly List<(long LastSeq, AuditAnchorReceipt Receipt)> _receipts = [];

        public List<AuditEntry> Entries { get; } = [];

        public string? FailAuditKind { get; set; }

        public int BeginCount { get; private set; }

        public int RollbackCount { get; private set; }

        public int InTransactionFailureChecks { get; private set; }

        public int OutsideFailureChecks { get; private set; }

        public IReadOnlyList<AuditAnchorReceipt> Receipts => _receipts.Select(r => r.Receipt).ToList();

        public IReadOnlyList<string> Kinds => Entries.Select(e => e.Kind).ToList();

        public Task AppendAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default)
        {
            _receipts.Add((checkpoint.LastSeq, receipt));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditAnchorReceipt>> ReadForCheckpointAsync(AuditCheckpoint checkpoint, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditAnchorReceipt>>(
                _receipts.Where(r => r.LastSeq == checkpoint.LastSeq).Select(r => r.Receipt).ToList());

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }

        public Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default)
        {
            OutsideFailureChecks++;
            return Task.FromResult(HasFailure(lastSeq, targetId));
        }

        public Task<IAuditAnchorAtomicScope> BeginAsync(CancellationToken ct = default)
        {
            BeginCount++;
            return Task.FromResult<IAuditAnchorAtomicScope>(new Scope(this));
        }

        private bool HasFailure(long lastSeq, string targetId) =>
            Entries.Any(e =>
            {
                if (e.Kind != AuditAnchorService.FailedKind)
                {
                    return false;
                }

                var payload = JsonNode.Parse(e.Payload)!;
                return payload["last_seq"]!.GetValue<long>() == lastSeq && payload["target_id"]!.GetValue<string>() == targetId;
            });

        private sealed class Scope(Pair owner) : IAuditAnchorAtomicScope
        {
            private readonly List<(long LastSeq, AuditAnchorReceipt Receipt)> _stagedReceipts = [];
            private readonly List<AuditEntry> _stagedEntries = [];
            private bool _committed;

            public Task AppendReceiptAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default)
            {
                _stagedReceipts.Add((checkpoint.LastSeq, receipt));
                return Task.CompletedTask;
            }

            public Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default)
            {
                owner.InTransactionFailureChecks++;
                return Task.FromResult(owner.HasFailure(lastSeq, targetId));
            }

            public Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default)
            {
                if (entry.Kind == owner.FailAuditKind)
                {
                    throw new InvalidOperationException("audit write failed");
                }

                _stagedEntries.Add(entry);
                return Task.CompletedTask;
            }

            public Task CommitAsync(CancellationToken ct = default)
            {
                owner._receipts.AddRange(_stagedReceipts);
                owner.Entries.AddRange(_stagedEntries);
                _committed = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed)
                {
                    owner.RollbackCount++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
