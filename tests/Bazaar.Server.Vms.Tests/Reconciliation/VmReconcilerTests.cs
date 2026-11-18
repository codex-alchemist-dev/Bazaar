using System.Text.Json;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.Reconciliation;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Vms.Tests.Reconciliation;

/// <summary>Startup reconciliation and quarantine against a real database and a scripted backend.</summary>
public sealed class VmReconcilerTests : ReconcileDb
{
    private static readonly string[] _staleCorrectionNames = ["e2e-corrected", "e2e-unexpected", "e2e-error", "e2e-matched", "e2e-restored"];
    private static readonly string[] _stateMismatchOnly = [VmAuditActions.StateMismatch];
    private static readonly string[] _backendErrorOnly = [VmAuditActions.BackendError];
    private static readonly string[] _mismatchAndUnexpected = [VmAuditActions.StateMismatch, VmAuditActions.UnexpectedState];
    private static readonly string[] _firstRunActions = [VmAuditActions.BackendError, VmAuditActions.StateMismatch, VmAuditActions.StateMismatch, VmAuditActions.StateMismatch, VmAuditActions.UnexpectedState];
    private static readonly string[] _secondRunActions = [VmAuditActions.BackendError, VmAuditActions.StateMismatch, VmAuditActions.UnexpectedState];

    private readonly FakeClock _clock = new(Epoch.AddHours(1));
    private readonly ScriptedVmBackend _backend = new();
    private readonly VmOperationLocks _locks = new();

    private VmReconciler NewReconciler() => new(Repository, _backend, _locks, AuditWriter, _clock);

    /// <summary>
    /// Checks each count. When <paramref name="actions"/> is given, the audit actions must match it in order. The actions are
    /// compared as arrays, since a record compares its lists by reference.
    /// </summary>
    private static void AssertReport(ReconcileReport report, int checkedCount, int matched, int corrected, int skipped, int errors, string[]? actions = null)
    {
        Assert.Equal(checkedCount, report.Checked);
        Assert.Equal(matched, report.Matched);
        Assert.Equal(corrected, report.Corrected);
        Assert.Equal(skipped, report.Skipped);
        Assert.Equal(errors, report.Errors);
        if (actions is not null)
            Assert.Equal(actions, report.AuditActions.ToArray());
    }

    public static TheoryData<VmState, VmState> AllowedMismatches() => new()
    {
        { VmState.Provisioning, VmState.Stopped },
        { VmState.Running, VmState.Stopped },
        { VmState.Stopped, VmState.Running },
        { VmState.Saved, VmState.Running },
        { VmState.Restored, VmState.Running },
        { VmState.Stopped, VmState.Retired },
    };

    public static TheoryData<VmState, VmState> ForbiddenMismatches() => new()
    {
        { VmState.Running, VmState.Absent },
        { VmState.Stopped, VmState.Absent },
        { VmState.Provisioning, VmState.Running },
        { VmState.Saved, VmState.Stopped },
    };

    [Fact]
    public async Task Matching_state_is_counted_and_nothing_is_written()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var vm = await InsertVmAsync(VmState.Running, "ref-match");
        _backend.Report("ref-match", VmState.Running);

        var report = await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        AssertReport(report, 1, 1, 0, 0, 0);
        Assert.Empty(await ReadAuditAsync());
        Assert.Equal(VmState.Running, await StateOfAsync(vm.Id));
        Assert.Equal("ref-match", Assert.Single(_backend.StateQueries));
        Assert.Empty(_backend.UnexpectedCalls);
    }

    [Theory]
    [MemberData(nameof(AllowedMismatches))]
    public async Task Allowed_mismatch_is_audited_and_then_corrected(VmState recorded, VmState actual)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var vm = await InsertVmAsync(recorded, "ref-allowed");
        _backend.Report("ref-allowed", actual);

        var report = await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        AssertReport(report, 1, 0, 1, 0, 0, _stateMismatchOnly);
        Assert.Equal(actual, await StateOfAsync(vm.Id));

        var row = Assert.Single(await ReadAuditAsync());
        Assert.Equal(VmAuditActions.StateMismatch, row.Kind);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(vm.Id.ToString(), payload.RootElement.GetProperty("subject").GetString());
        Assert.Equal(recorded.ToString(), payload.RootElement.GetProperty("detail").GetProperty("recorded").GetString());
        Assert.Equal(actual.ToString(), payload.RootElement.GetProperty("detail").GetProperty("actual").GetString());
        Assert.True(ChainIsValid(await ReadAuditAsync()));
    }

    [Fact]
    public async Task Corrected_row_gets_the_reconcile_time_as_updated_at()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var vm = await InsertVmAsync(VmState.Stopped, "ref-time");
        _backend.Report("ref-time", VmState.Running);

        await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        var row = await Repository.GetVmAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(_clock.GetUtcNow(), row!.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(ForbiddenMismatches))]
    public async Task Forbidden_mismatch_is_audited_as_unexpected_and_the_row_is_left_alone(VmState recorded, VmState actual)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var vm = await InsertVmAsync(recorded, "ref-forbidden");
        _backend.Report("ref-forbidden", actual);

        var report = await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        AssertReport(report, 1, 0, 0, 1, 0, _mismatchAndUnexpected);
        Assert.Equal(recorded, await StateOfAsync(vm.Id));

        var rows = await ReadAuditAsync();
        Assert.Equal(_mismatchAndUnexpected, rows.Select(r => r.Kind).ToArray());
        Assert.True(ChainIsValid(rows));
    }

    [Fact]
    public async Task Backend_error_is_audited_with_the_message_and_the_row_is_untouched()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var vm = await InsertVmAsync(VmState.Stopped, "ref-error");
        _backend.Fail("ref-error", new InvalidOperationException("hyper-v timed out"));

        var report = await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        AssertReport(report, 1, 0, 0, 0, 1, _backendErrorOnly);
        Assert.Equal(VmState.Stopped, await StateOfAsync(vm.Id));
        var row = await Repository.GetVmAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Epoch, row!.UpdatedAt);

        var audit = Assert.Single(await ReadAuditAsync());
        Assert.Equal(VmAuditActions.BackendError, audit.Kind);
        using var payload = JsonDocument.Parse(audit.Payload);
        Assert.Equal("hyper-v timed out", payload.RootElement.GetProperty("detail").GetProperty("message").GetString());
        Assert.Equal("Stopped", payload.RootElement.GetProperty("detail").GetProperty("recorded").GetString());
    }

    [Fact]
    public async Task Retired_vms_are_not_checked_at_all()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await InsertVmAsync(VmState.Retired, "ref-retired");
        await InsertVmAsync(VmState.Running, "ref-live");
        _backend.Report("ref-live", VmState.Running);

        var report = await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Checked);
        Assert.Equal("ref-live", Assert.Single(_backend.StateQueries));
    }

    [Fact]
    public async Task Vm_without_a_backend_ref_is_skipped_and_the_backend_is_not_asked()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var vm = await InsertVmAsync(VmState.Absent, backendRef: null);

        var report = await NewReconciler().ReconcileAsync(TestContext.Current.CancellationToken);

        AssertReport(report, 1, 0, 0, 1, 0);
        Assert.Empty(_backend.StateQueries);
        Assert.Equal(VmState.Absent, await StateOfAsync(vm.Id));
    }

    [Fact]
    public async Task Reconcile_waits_for_the_vm_lock_and_does_not_touch_the_backend_while_it_is_held()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var vm = await InsertVmAsync(VmState.Running, "ref-locked");
        _backend.Report("ref-locked", VmState.Stopped);

        using var lease = await _locks.AcquireAsync(vm.Id, ct);
        using var cancel = new CancellationTokenSource();
        var pending = NewReconciler().ReconcileAsync(cancel.Token);
        await cancel.CancelAsync();

        // The lock is held, so the reconcile cannot finish. Cancelling it while it waits is the only way it ends.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(_backend.StateQueries);
        Assert.Empty(await ReadAuditAsync());
        Assert.Equal(VmState.Running, await StateOfAsync(vm.Id));

        lease.Dispose();
        Assert.Equal(0, _locks.ActiveCount);

        var report = await NewReconciler().ReconcileAsync(ct);
        Assert.Equal(1, report.Corrected);
        Assert.Equal(VmState.Stopped, await StateOfAsync(vm.Id));
    }

    [Fact]
    public async Task Quarantine_audits_orphans_and_passes_over_handles_that_have_a_record()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var known = await InsertVmAsync(VmState.Running, "ref-known");
        var orphan = new VmHandle(Guid.CreateVersion7(), "hyperv:orphan-1");

        var quarantined = await NewReconciler().QuarantineAsync(
            [orphan, new VmHandle(known.Id, "ref-known")], ct);

        Assert.Equal(orphan, Assert.Single(quarantined));
        var row = Assert.Single(await ReadAuditAsync());
        Assert.Equal(VmAuditActions.OrphanBackendVm, row.Kind);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(orphan.VmId.ToString(), payload.RootElement.GetProperty("subject").GetString());
        Assert.Equal("hyperv:orphan-1", payload.RootElement.GetProperty("detail").GetProperty("backend_ref").GetString());

        Assert.Equal(VmState.Running, await StateOfAsync(known.Id));
        Assert.Empty(_backend.StateQueries);
        Assert.Empty(_backend.UnexpectedCalls);
    }

    [Fact]
    public async Task Quarantine_with_no_handles_writes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var quarantined = await NewReconciler().QuarantineAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(quarantined);
        Assert.Empty(await ReadAuditAsync());
    }

    [Fact]
    public async Task End_to_end_run_corrects_audits_and_isolates_each_failure_then_a_rerun_sees_the_corrections()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var corrected = await InsertVmAsync(VmState.Stopped, "e2e-corrected");
        var unexpected = await InsertVmAsync(VmState.Running, "e2e-unexpected");
        var errored = await InsertVmAsync(VmState.Stopped, "e2e-error");
        var matched = await InsertVmAsync(VmState.Stopped, "e2e-matched");
        var restored = await InsertVmAsync(VmState.Restored, "e2e-restored");
        await InsertVmAsync(VmState.Retired, "e2e-retired");
        var noRef = await InsertVmAsync(VmState.Absent, backendRef: null);

        _backend.Report("e2e-corrected", VmState.Running);
        _backend.Report("e2e-unexpected", VmState.Absent);
        _backend.Fail("e2e-error", new InvalidOperationException("provider unreachable"));
        _backend.Report("e2e-matched", VmState.Stopped);
        _backend.Report("e2e-restored", VmState.Running);

        var first = await NewReconciler().ReconcileAsync(ct);

        AssertReport(first, 6, 1, 2, 2, 1);
        Assert.Equal(
            _firstRunActions.Order(StringComparer.Ordinal),
            first.AuditActions.Order(StringComparer.Ordinal));

        Assert.Equal(VmState.Running, await StateOfAsync(corrected.Id));
        Assert.Equal(VmState.Running, await StateOfAsync(unexpected.Id));
        Assert.Equal(VmState.Stopped, await StateOfAsync(errored.Id));
        Assert.Equal(VmState.Stopped, await StateOfAsync(matched.Id));
        Assert.Equal(VmState.Running, await StateOfAsync(restored.Id));
        Assert.Equal(VmState.Absent, await StateOfAsync(noRef.Id));
        Assert.Equal(
            _staleCorrectionNames.Order(StringComparer.Ordinal),
            _backend.StateQueries.Order(StringComparer.Ordinal));

        var rows = await ReadAuditAsync();
        Assert.Equal(5, rows.Count);
        Assert.True(ChainIsValid(rows));

        // The second run sees the corrections: the corrected VMs match now, and the forbidden and failing ones are reported again.
        var second = await NewReconciler().ReconcileAsync(ct);

        AssertReport(second, 6, 3, 0, 2, 1);
        Assert.Equal(
            _secondRunActions.Order(StringComparer.Ordinal),
            second.AuditActions.Order(StringComparer.Ordinal));
        var allRows = await ReadAuditAsync();
        Assert.Equal(8, allRows.Count);
        Assert.True(ChainIsValid(allRows));
    }
}
