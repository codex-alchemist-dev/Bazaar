using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Checkpoints;
using Bazaar.Server.Security.Audit.Db;

namespace Bazaar.Server.Security.Tests.Audit.Anchors;

/// <summary>AuditAnchorService and AuditAnchorOptions with in-memory fakes: failures are audited and not thrown, recovery re-anchors.</summary>
public sealed class AuditAnchorServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditAnchorService Service(
        IReadOnlyList<IAuditAnchorTarget> targets,
        InMemoryAnchorStore store,
        RecordingAuditLog audit,
        TimeSpan? timeout = null) =>
        new(targets, store, audit, new AuditLogFailureLog(audit), new FixedTime(), timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Every_target_anchors_the_digest_of_the_signed_bytes_and_stores_its_receipt()
    {
        var stored = AnchorFixtures.Stored();
        var first = new ScriptedTarget("rfc3161");
        var second = new ScriptedTarget("offsite-git");
        var store = new InMemoryAnchorStore();
        var audit = new RecordingAuditLog();

        var outcome = await Service([first, second], store, audit).AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161", "offsite-git"], outcome.Anchored);
        Assert.Empty(outcome.Failed);
        Assert.Equal(AnchorFixtures.DigestOf(stored.Checkpoint), first.LastDigest);
        Assert.Equal(2, (await store.ReadForCheckpointAsync(stored.Checkpoint, Ct)).Count);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_failing_target_is_audited_and_not_thrown_and_the_others_still_anchor()
    {
        var stored = AnchorFixtures.Stored();
        var failing = new ScriptedTarget("rfc3161") { AnchorFailure = new HttpRequestException("https://tsa.example.invalid/tsr refused") };
        var working = new ScriptedTarget("offsite-git");
        var store = new InMemoryAnchorStore();
        var audit = new RecordingAuditLog();

        var outcome = await Service([failing, working], store, audit).AnchorAsync(stored, Ct);

        Assert.Equal(["offsite-git"], outcome.Anchored);
        Assert.Equal(["rfc3161"], outcome.Failed);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAnchorService.FailedKind, entry.Kind);
        Assert.Equal("system", entry.Actor);
        Assert.Contains("\"target_id\":\"rfc3161\"", entry.Payload);
        Assert.Contains("\"reason\":\"HttpRequestException\"", entry.Payload);
        // The exception message can carry a URL, so it must not reach the audit payload.
        Assert.DoesNotContain("tsa.example.invalid", entry.Payload);
        Assert.Single(await store.ReadForCheckpointAsync(stored.Checkpoint, Ct));
    }

    [Fact]
    public async Task A_later_success_anchors_the_same_checkpoint_again_and_records_recovery()
    {
        var stored = AnchorFixtures.Stored();
        var flaky = new ScriptedTarget("rfc3161") { AnchorFailure = new HttpRequestException("down") };
        var steady = new ScriptedTarget("offsite-git");
        var store = new InMemoryAnchorStore();
        var audit = new RecordingAuditLog();
        var service = Service([flaky, steady], store, audit);

        await service.AnchorAsync(stored, Ct);
        flaky.AnchorFailure = null;
        var outcome = await service.AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal(AuditAnchorService.FailedKind, audit.Entries[0].Kind);
        Assert.Equal(AuditAnchorService.RecoveredKind, audit.Entries[1].Kind);
        Assert.Contains("\"target_id\":\"rfc3161\"", audit.Entries[1].Payload);
        Assert.Equal(2, (await store.ReadForCheckpointAsync(stored.Checkpoint, Ct)).Count);
        // The target that already holds a receipt is skipped, not anchored a second time.
        Assert.Equal(1, steady.AnchorCalls);
    }

    [Fact]
    public async Task A_new_service_instance_after_a_restart_still_records_recovery_from_the_audit_log()
    {
        var stored = AnchorFixtures.Stored();
        var flaky = new ScriptedTarget("rfc3161") { AnchorFailure = new HttpRequestException("down") };
        var store = new InMemoryAnchorStore();
        var audit = new RecordingAuditLog();
        await Service([flaky], store, audit).AnchorAsync(stored, Ct);

        // Restart: the new instance has no in-memory failure state, only the audit log the first instance wrote.
        flaky.AnchorFailure = null;
        var outcome = await Service([flaky], store, audit).AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Equal([AuditAnchorService.FailedKind, AuditAnchorService.RecoveredKind], audit.Entries.Select(e => e.Kind));
        var recovered = JsonNode.Parse(audit.Entries[1].Payload)!;
        Assert.Equal(stored.Checkpoint.LastSeq, recovered["last_seq"]!.GetValue<long>());
        Assert.Equal("rfc3161", recovered["target_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failure_recorded_for_another_target_or_checkpoint_does_not_cause_a_recovery()
    {
        var stored = AnchorFixtures.Stored();
        var target = new ScriptedTarget("rfc3161");
        var audit = new RecordingAuditLog();
        audit.Entries.Add(new AuditEntry(AnchorFixtures.Now, "system", AuditAnchorService.FailedKind, "{\"last_seq\": 5, \"target_id\": \"offsite-git\", \"reason\": \"timeout\"}"));
        audit.Entries.Add(new AuditEntry(AnchorFixtures.Now, "system", AuditAnchorService.FailedKind, "{\"last_seq\": 4, \"target_id\": \"rfc3161\", \"reason\": \"timeout\"}"));

        var outcome = await Service([target], new InMemoryAnchorStore(), audit).AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Anchored);
        Assert.Equal(2, audit.Entries.Count);
    }

    [Fact]
    public async Task A_recovered_target_is_not_reported_as_recovered_twice()
    {
        var stored = AnchorFixtures.Stored();
        var flaky = new ScriptedTarget("rfc3161") { AnchorFailure = new HttpRequestException("down") };
        var audit = new RecordingAuditLog();
        var service = Service([flaky], new InMemoryAnchorStore(), audit);

        await service.AnchorAsync(stored, Ct);
        flaky.AnchorFailure = null;
        await service.AnchorAsync(stored, Ct);
        await service.AnchorAsync(stored, Ct);

        Assert.Equal(2, audit.Entries.Count);
    }

    [Fact]
    public async Task A_receipt_that_does_not_verify_is_recorded_as_a_failure_and_not_stored()
    {
        var stored = AnchorFixtures.Stored();
        var target = new ScriptedTarget("rfc3161") { VerifyResult = false };
        var store = new InMemoryAnchorStore();
        var audit = new RecordingAuditLog();

        var outcome = await Service([target], store, audit).AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Failed);
        Assert.Empty(await store.ReadForCheckpointAsync(stored.Checkpoint, Ct));
        Assert.Contains("\"reason\":\"receipt_not_verified\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task A_target_that_answers_too_late_is_recorded_as_a_timeout()
    {
        var stored = AnchorFixtures.Stored();
        var slow = new ScriptedTarget("rfc3161") { Hangs = true };
        var audit = new RecordingAuditLog();

        var outcome = await Service([slow], new InMemoryAnchorStore(), audit, timeout: TimeSpan.FromSeconds(1)).AnchorAsync(stored, Ct);

        Assert.Equal(["rfc3161"], outcome.Failed);
        Assert.Contains("\"reason\":\"timeout\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task Host_cancellation_is_not_swallowed()
    {
        var stored = AnchorFixtures.Stored();
        var hanging = new ScriptedTarget("rfc3161") { Hangs = true };
        var audit = new RecordingAuditLog();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service([hanging], new InMemoryAnchorStore(), audit).AnchorAsync(stored, cts.Token));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void A_service_with_no_targets_or_an_out_of_range_timeout_is_refused()
    {
        var store = new InMemoryAnchorStore();
        var audit = new RecordingAuditLog();

        Assert.Throws<ArgumentException>(() => Service([], store, audit));
        Assert.Throws<ArgumentOutOfRangeException>(() => Service([new ScriptedTarget("a")], store, audit, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Service([new ScriptedTarget("a")], store, audit, TimeSpan.FromSeconds(121)));
    }

    [Fact]
    public void Anchoring_is_off_by_default_and_creates_no_targets()
    {
        var options = new AuditAnchorOptions();

        options.Validate();
        Assert.Empty(options.CreateTargets(new HttpClient(), new NoGit(), new FixedTime()));
    }

    [Fact]
    public void Enabled_anchoring_with_no_enabled_target_is_refused()
    {
        var options = new AuditAnchorOptions
        {
            Enabled = true,
            Rfc3161 = new Rfc3161TargetOptions { Enabled = false, Endpoint = "https://tsa.example.invalid/tsr" },
            OffsiteGit = new OffsiteGitTargetOptions { Enabled = false, LocalPath = "/srv/anchors" },
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Enabled_anchoring_with_an_unconfigured_target_is_refused()
    {
        // The shipped default enables both targets but gives neither a location, so turning anchoring on without configuring them fails.
        var options = new AuditAnchorOptions { Enabled = true };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void An_enabled_configured_target_is_created_and_a_disabled_one_is_left_out()
    {
        var options = new AuditAnchorOptions
        {
            Enabled = true,
            Rfc3161 = new Rfc3161TargetOptions { Endpoint = "https://tsa.example.invalid/tsr" },
            OffsiteGit = new OffsiteGitTargetOptions { Enabled = false },
        };

        var targets = options.CreateTargets(new HttpClient(), new NoGit(), new FixedTime());

        Assert.Equal(["rfc3161"], targets.Select(t => t.TargetId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void A_target_timeout_outside_its_bounds_is_refused(int seconds)
    {
        var options = new AuditAnchorOptions { TargetTimeout = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    /// <summary>A target whose behaviour a test sets: failure, verify result, or hanging until cancelled.</summary>
    private sealed class ScriptedTarget(string targetId) : IAuditAnchorTarget
    {
        public string TargetId => targetId;

        public Exception? AnchorFailure { get; set; }

        public bool VerifyResult { get; set; } = true;

        public bool Hangs { get; set; }

        public int AnchorCalls { get; private set; }

        public byte[]? LastDigest { get; private set; }

        public async Task<AuditAnchorReceipt> AnchorAsync(ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct)
        {
            AnchorCalls++;
            LastDigest = checkpointDigest.ToArray();
            if (Hangs)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (AnchorFailure is not null)
            {
                throw AnchorFailure;
            }

            return new AuditAnchorReceipt(targetId, checkpointDigest.ToArray(), AnchorFixtures.Now);
        }

        public Task<bool> VerifyAsync(AuditAnchorReceipt receipt, ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct) =>
            Task.FromResult(VerifyResult);
    }

    /// <summary>Anchor receipts held in memory, keyed by checkpoint last sequence and target.</summary>
    private sealed class InMemoryAnchorStore : IAuditAnchorStore
    {
        private readonly List<(long LastSeq, AuditAnchorReceipt Receipt)> _rows = [];

        public Task AppendAsync(AuditCheckpoint checkpoint, AuditAnchorReceipt receipt, CancellationToken ct = default)
        {
            if (_rows.Any(r => r.LastSeq == checkpoint.LastSeq && r.Receipt.TargetId == receipt.TargetId))
            {
                throw new InvalidOperationException("Duplicate receipt for this target.");
            }

            _rows.Add((checkpoint.LastSeq, receipt));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditAnchorReceipt>> ReadForCheckpointAsync(AuditCheckpoint checkpoint, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditAnchorReceipt>>(
                _rows.Where(r => r.LastSeq == checkpoint.LastSeq).Select(r => r.Receipt).OrderBy(r => r.TargetId, StringComparer.Ordinal).ToList());
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => AnchorFixtures.Now;
    }

    /// <summary>Answers from the failure rows in the recording log, as the Postgres reader answers from <c>audit_log</c>.</summary>
    private sealed class AuditLogFailureLog(RecordingAuditLog audit) : IAuditAnchorFailureLog
    {
        public Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default) =>
            Task.FromResult(audit.Entries.Any(e =>
            {
                if (e.Kind != AuditAnchorService.FailedKind)
                {
                    return false;
                }

                var payload = JsonNode.Parse(e.Payload)!;
                return payload["last_seq"]!.GetValue<long>() == lastSeq && payload["target_id"]!.GetValue<string>() == targetId;
            }));
    }

    private sealed class NoGit : IGitRunner
    {
        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
            throw new InvalidOperationException("No git call expected.");
    }
}
