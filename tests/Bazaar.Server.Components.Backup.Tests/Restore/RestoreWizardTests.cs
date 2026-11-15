using System.Text.Json;
using Bazaar.Server.Components.Backup.Recovery;
using Bazaar.Server.Components.Backup.Restore;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Components.Backup.Tests.Restore;

/// <summary>Writes one sealed bundle for the class (PBKDF2 at the floor is slow, so tests share it).</summary>
public sealed class RestoreBundleFixture : IDisposable
{
    public const string Passphrase = "restore wizard test passphrase";
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();

    public RestoreBundleFixture()
    {
        Directory.CreateDirectory(_dir.Path);
        var rows = new List<AuditRecord>();
        AuditRecord? head = null;
        for (var i = 1; i <= 6; i++)
        {
            head = AuditChain.Append(head, _start.AddSeconds(i), "agent", "turn.post", $"payload-{i}");
            rows.Add(head);
        }

        After = rows.Skip(3).ToList();
        var root = new MerkleTree(After.Select(r => r.Hash)).Root;
        Anchor = new AuditCheckpoint(1, 3, 3, rows[2].Hash, root, _start);

        BundlePath = Path.Combine(_dir.Path, "bundle.bzrb");
        BundlePathWithoutKek = Path.Combine(_dir.Path, "nokek.bzrb");
        BundlePathWithoutHead = Path.Combine(_dir.Path, "nohead.bzrb");
        RecoveryBundleFile.Write(BundlePath, Payload(kek: [7, 7, 7], head: Anchor.ChainHeadHash), Passphrase, KdfOptions.Default);
        RecoveryBundleFile.Write(BundlePathWithoutKek, Payload(kek: [], head: Anchor.ChainHeadHash), Passphrase, KdfOptions.Default);
        RecoveryBundleFile.Write(BundlePathWithoutHead, Payload(kek: [7], head: null), Passphrase, KdfOptions.Default);
    }

    public string BundlePath { get; }

    public string BundlePathWithoutKek { get; }

    public string BundlePathWithoutHead { get; }

    public AuditCheckpoint Anchor { get; }

    public List<AuditRecord> After { get; }

    public static string SetSha => new('a', 64);

    public void Dispose() => _dir.Dispose();

    private static RecoveryBundlePayload Payload(byte[] kek, byte[]? head) =>
        new(
            RecoveryBundlePayload.CurrentVersion,
            new Dictionary<string, string>(),
            kek,
            head,
            [new RecoveryBackupSetEntry("base-1", SetSha, 1024)],
            [],
            []);
}

public sealed class RestoreWizardTests(RestoreBundleFixture fixture) : IClassFixture<RestoreBundleFixture>
{
    [Fact]
    public async Task Happy_path_restores_in_order_and_waits_for_owner_before_writes()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request(), CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(RestoreState.AwaitingOwnerConfirmation, wizard.State);
        Assert.Equal(
            ["read-only", "keys", "database", "soul", "vault", "indexes"],
            h.Calls);
        Assert.False(h.WritesEnabled);
        Assert.Equal(3, outcome.AuditVerification!.RowsChecked);
        Assert.Equal([7, 7, 7], h.KeysSeen);
        Assert.All(h.Events, e => Assert.Contains(wizard.RestoreId, e.Payload, StringComparison.Ordinal));
        Assert.Equal("restore.started", h.Events[0].Kind);
        Assert.Equal("restore.read_only", h.Events[^1].Kind);
    }

    [Fact]
    public async Task Confirmation_enables_writes_after_auditing_it()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();
        await wizard.RunAsync(h.Request(), CancellationToken.None);

        var outcome = await wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, "owner"), CancellationToken.None);

        Assert.Equal(RestoreState.WritesEnabled, outcome.State);
        Assert.True(h.WritesEnabled);
        Assert.Equal("restore.writes_confirmed", h.Events[^1].Kind);
        Assert.True(h.AuditedBeforeEnable);
    }

    [Fact]
    public async Task Confirmation_before_run_or_with_wrong_id_is_refused_and_writes_stay_off()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, "owner"), CancellationToken.None));

        await wizard.RunAsync(h.Request(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wizard.ConfirmWritesAsync(new OwnerWriteConfirmation("someone-else", "owner"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, " "), CancellationToken.None));

        Assert.False(h.WritesEnabled);
        Assert.Equal(RestoreState.AwaitingOwnerConfirmation, wizard.State);
    }

    [Fact]
    public async Task Second_confirmation_is_refused()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();
        await wizard.RunAsync(h.Request(), CancellationToken.None);
        await wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, "owner"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, "owner"), CancellationToken.None));
    }

    [Fact]
    public async Task Failed_gate_on_confirmation_leaves_install_read_only_and_retryable()
    {
        var h = new Harness(fixture) { EnableWritesThrows = true };
        var wizard = h.Wizard();
        await wizard.RunAsync(h.Request(), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, "owner"), CancellationToken.None));

        Assert.Equal(RestoreState.AwaitingOwnerConfirmation, wizard.State);
        Assert.False(h.WritesEnabled);
    }

    [Fact]
    public async Task Wizard_runs_only_once()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();
        await wizard.RunAsync(h.Request(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wizard.RunAsync(h.Request(), CancellationToken.None));
    }

    [Fact]
    public async Task Populated_install_without_confirmation_is_refused_before_any_write()
    {
        var h = new Harness(fixture) { Populated = true };
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("check-target", outcome.FailedStep);
        Assert.Empty(h.Calls);
        Assert.False(h.Preserved);
        Assert.Contains(h.Events, e => e.Kind == "restore.failed");
    }

    [Fact]
    public async Task Populated_install_with_confirmation_for_another_restore_is_refused()
    {
        var h = new Harness(fixture) { Populated = true };
        var wizard = h.Wizard();
        var request = h.Request() with { OverwriteConfirmation = new OwnerOverwriteConfirmation("other-restore", "owner") };

        var outcome = await wizard.RunAsync(request, CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Populated_install_without_preserver_is_refused_even_when_confirmed()
    {
        var h = new Harness(fixture) { Populated = true, NoPreserver = true };
        var wizard = h.Wizard();
        var request = h.Request() with { OverwriteConfirmation = new OwnerOverwriteConfirmation(wizard.RestoreId, "owner") };

        var outcome = await wizard.RunAsync(request, CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Populated_install_with_owner_confirmation_preserves_live_data_then_restores()
    {
        var h = new Harness(fixture) { Populated = true };
        var wizard = h.Wizard();
        var request = h.Request() with { OverwriteConfirmation = new OwnerOverwriteConfirmation(wizard.RestoreId, "owner") };

        var outcome = await wizard.RunAsync(request, CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.True(h.Preserved);
        Assert.Equal("read-only", h.Calls[0]);
        Assert.Equal("preserve", h.Calls[1]);
        Assert.Contains(h.Events, e => e.Kind == "restore.overwrite_confirmed");
    }

    [Fact]
    public async Task Wrong_passphrase_fails_without_touching_the_install()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request() with { Passphrase = "wrong" }, CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("verify-bundle", outcome.FailedStep);
        Assert.Empty(h.Calls);
        Assert.DoesNotContain(h.Events, e => e.Payload.Contains("wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_bundle_file_fails_closed()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request() with { BundlePath = fixture.BundlePath + ".missing" }, CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Bundle_without_audit_head_is_refused()
    {
        var h = new Harness(fixture);
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request() with { BundlePath = fixture.BundlePathWithoutHead }, CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Bundle_without_kek_is_refused_by_default_and_allowed_by_option()
    {
        var refused = new Harness(fixture);
        var outcome = await refused.Wizard().RunAsync(refused.Request() with { BundlePath = fixture.BundlePathWithoutKek }, CancellationToken.None);
        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Empty(refused.Calls);

        var allowed = new Harness(fixture);
        var wizard = allowed.Wizard(new RestoreWizardOptions { AllowBundleWithoutKekExport = true });
        var ok = await wizard.RunAsync(allowed.Request() with { BundlePath = fixture.BundlePathWithoutKek }, CancellationToken.None);
        Assert.True(ok.IsSuccess);
        Assert.DoesNotContain("keys", allowed.Calls);
    }

    [Fact]
    public async Task Backup_set_checksum_mismatch_fails_before_read_only_or_any_restore()
    {
        var h = new Harness(fixture) { SetSha = new string('b', 64) };
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("verify-backup-sets", outcome.FailedStep);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Missing_backup_set_fails()
    {
        var h = new Harness(fixture) { SetMissing = true };
        var outcome = await h.Wizard().RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("verify-backup-sets", outcome.FailedStep);
    }

    [Fact]
    public async Task Step_failure_stops_the_run_leaves_writes_off_and_hides_exception_text()
    {
        var h = new Harness(fixture) { DatabaseThrows = true };
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("restore-database", outcome.FailedStep);
        Assert.DoesNotContain("secret-path", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("soul", h.Calls);
        Assert.False(h.WritesEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wizard.ConfirmWritesAsync(new OwnerWriteConfirmation(wizard.RestoreId, "owner"), CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_chain_after_anchor_fails_closed_and_is_audited()
    {
        var h = new Harness(fixture);
        h.Rows[1] = h.Rows[1] with { Payload = "tampered" };
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("verify-audit-chain", outcome.FailedStep);
        Assert.Equal(RestoreAuditReason.HashMismatch, outcome.AuditVerification!.Reason);
        var failed = Assert.Single(h.Events, e => e.Kind == "restore.audit_verification_failed");
        Assert.Contains("HashMismatch", failed.Payload, StringComparison.Ordinal);
        Assert.False(h.WritesEnabled);
    }

    [Fact]
    public async Task Bad_checkpoint_signature_fails_closed()
    {
        var h = new Harness(fixture) { Signature = [9, 9] };
        var outcome = await h.Wizard().RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal(RestoreAuditReason.SignatureInvalid, outcome.AuditVerification!.Reason);
        Assert.Contains(h.Events, e => e.Kind == "restore.audit_verification_failed");
    }

    [Fact]
    public async Task Anchor_head_that_differs_from_bundle_head_fails_closed()
    {
        var h = new Harness(fixture);
        var head = (byte[])fixture.Anchor.ChainHeadHash.Clone();
        head[0] ^= 0xFF;
        h.Anchor = fixture.Anchor with { ChainHeadHash = head };

        var outcome = await h.Wizard().RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Equal("verify-audit-chain", outcome.FailedStep);
        Assert.Contains(h.Events, e => e.Kind == "restore.audit_verification_failed");
    }

    [Fact]
    public async Task Missing_anchor_fails_closed()
    {
        var h = new Harness(fixture) { NoEvidence = true };
        var outcome = await h.Wizard().RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Contains(h.Events, e => e.Kind == "restore.audit_verification_failed");
        Assert.False(h.WritesEnabled);
    }

    [Fact]
    public async Task Audit_sink_failure_at_start_stops_before_any_step()
    {
        var h = new Harness(fixture) { AuditThrows = true };
        var wizard = h.Wizard();

        var outcome = await wizard.RunAsync(h.Request(), CancellationToken.None);

        Assert.Equal(RestoreState.Failed, outcome.State);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Audit_payloads_never_contain_passphrase_or_key_bytes()
    {
        var h = new Harness(fixture);
        await h.Wizard().RunAsync(h.Request(), CancellationToken.None);

        foreach (var e in h.Events)
        {
            using var doc = JsonDocument.Parse(e.Payload);
            Assert.DoesNotContain(RestoreBundleFixture.Passphrase, e.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("kek", e.Payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Non_utc_point_in_time_is_rejected()
    {
        var h = new Harness(fixture);
        var request = h.Request() with { PointInTime = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(2)) };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Wizard().RunAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Point_in_time_reaches_the_database_step_and_the_start_event()
    {
        var h = new Harness(fixture);
        var pit = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

        await h.Wizard().RunAsync(h.Request() with { PointInTime = pit }, CancellationToken.None);

        Assert.Equal(pit, h.PointSeen);
        Assert.Contains("2026-11-02", h.Events[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_marks_the_restore_failed()
    {
        var h = new Harness(fixture) { CancelInDatabase = true };
        var wizard = h.Wizard();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wizard.RunAsync(h.Request(), CancellationToken.None));

        Assert.Equal(RestoreState.Failed, wizard.State);
        Assert.False(h.WritesEnabled);
    }

    /// <summary>Fakes for every wizard dependency, recording the order of the restore calls.</summary>
    private sealed class Harness : IRestoreTargetProbe, ILiveDataPreserver, IRestoreBackupSource, IRestoreKeyRestorer,
        IRestoreDatabaseRestorer, IRestoreFilesRestorer, IRestoreIndexRebuilder, IRestoreAuditEvidenceSource,
        IRestoreAuditSink, IRestoreWriteGate, IAuditCheckpointSigner
    {
        private readonly RestoreBundleFixture _fixture;

        public Harness(RestoreBundleFixture fixture)
        {
            _fixture = fixture;
            Anchor = fixture.Anchor;
            Rows = [.. fixture.After];
        }

        public List<string> Calls { get; } = [];

        public List<RecoveryAuditEvent> Events { get; } = [];

        public bool Populated { get; init; }

        public bool NoPreserver { get; init; }

        public bool Preserved { get; private set; }

        public bool WritesEnabled { get; private set; }

        public bool AuditedBeforeEnable { get; private set; }

        public bool EnableWritesThrows { get; init; }

        public bool DatabaseThrows { get; init; }

        public bool CancelInDatabase { get; init; }

        public bool AuditThrows { get; init; }

        public bool NoEvidence { get; init; }

        public bool SetMissing { get; init; }

        public string SetSha { get; init; } = RestoreBundleFixture.SetSha;

        public byte[] Signature { get; init; } = [1, 2, 3, 4];

        public AuditCheckpoint Anchor { get; set; }

        public List<AuditRecord> Rows { get; }

        public byte[]? KeysSeen { get; private set; }

        public DateTimeOffset? PointSeen { get; private set; }

        string IAuditCheckpointSigner.KeyId => "test-key";

        public RestoreWizard Wizard(RestoreWizardOptions? options = null) =>
            new(this, NoPreserver ? null : this, this, this, this, this, this, this, this, this, this, options);

        public RestoreWizardRequest Request() => new(_fixture.BundlePath, RestoreBundleFixture.Passphrase);

        public Task<bool> IsPopulatedAsync(CancellationToken ct) => Task.FromResult(Populated);

        public Task PreserveAsync(string restoreId, CancellationToken ct)
        {
            Preserved = true;
            Calls.Add("preserve");
            return Task.CompletedTask;
        }

        public Task<(string Sha256Hex, long SizeBytes)?> DescribeAsync(string name, CancellationToken ct) =>
            Task.FromResult<(string, long)?>(SetMissing ? null : (SetSha, 1024));

        public Task RestoreKeysAsync(ReadOnlyMemory<byte> kekExport, CancellationToken ct)
        {
            KeysSeen = kekExport.ToArray();
            Calls.Add("keys");
            return Task.CompletedTask;
        }

        public Task RestoreDatabaseAsync(IReadOnlyList<RecoveryBackupSetEntry> sets, DateTimeOffset? pointInTime, CancellationToken ct)
        {
            if (CancelInDatabase)
            {
                throw new OperationCanceledException();
            }

            if (DatabaseThrows)
            {
                throw new IOException("secret-path /var/lib/secret");
            }

            PointSeen = pointInTime;
            Calls.Add("database");
            return Task.CompletedTask;
        }

        public Task RestoreSoulFoldersAsync(IReadOnlyList<RecoveryBackupSetEntry> sets, CancellationToken ct)
        {
            Calls.Add("soul");
            return Task.CompletedTask;
        }

        public Task RestoreVaultAsync(IReadOnlyList<RecoveryBackupSetEntry> sets, CancellationToken ct)
        {
            Calls.Add("vault");
            return Task.CompletedTask;
        }

        public Task RebuildAsync(CancellationToken ct)
        {
            Calls.Add("indexes");
            return Task.CompletedTask;
        }

        public Task<RestoreAuditEvidence?> LoadAsync(byte[] bundleHead, CancellationToken ct) =>
            Task.FromResult<RestoreAuditEvidence?>(NoEvidence ? null : new RestoreAuditEvidence(Anchor, Signature, Rows));

        public Task AppendAsync(RecoveryAuditEvent auditEvent, CancellationToken ct)
        {
            if (AuditThrows)
            {
                throw new IOException("audit down");
            }

            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task EnterReadOnlyAsync(string restoreId, CancellationToken ct)
        {
            Calls.Add("read-only");
            return Task.CompletedTask;
        }

        public Task EnableWritesAsync(string restoreId, CancellationToken ct)
        {
            if (EnableWritesThrows)
            {
                throw new IOException("gate down");
            }

            AuditedBeforeEnable = Events.Count > 0 && Events[^1].Kind == "restore.writes_confirmed";
            WritesEnabled = true;
            return Task.CompletedTask;
        }

        byte[] IAuditCheckpointSigner.Sign(ReadOnlyMemory<byte> data) => [1, 2, 3, 4];

        bool IAuditCheckpointSigner.Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature) =>
            signature.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 });
    }
}
