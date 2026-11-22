using System.Text.Json;
using Bazaar.Admin;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Admin.Tests;

/// <summary>
/// A gate that counts what it registers and whether the applier's instance lock is touched. The lock throws if it is ever
/// acquired while <c>open</c> is false, so a closed gate that let the applier run would fail the test.
/// </summary>
internal sealed class CountingApplyGate(bool open) : ICliApplyGate
{
    public bool DurableAuditStoreAvailable => open;

    /// <summary>How many times the gate registered apply services. A closed gate must leave this at zero.</summary>
    public int RegistrationCount { get; private set; }

    /// <summary>The lock the applier would take. Its acquisitions are the proof the applier ran.</summary>
    public CountingLock Lock { get; } = new();

    public void RegisterApplyServices(IServiceCollection services)
    {
        RegistrationCount++;
        services.AddSingleton<ISetupInstanceLock>(Lock);
        services.AddSingleton<IAuditLog>(new NoopAuditLog());
        services.AddSingleton<ISettingAuditSink>(new InMemorySettingAuditSink());
    }

    public sealed class CountingLock : ISetupInstanceLock
    {
        public int Acquisitions { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            Acquisitions++;
            return Task.FromResult<IAsyncDisposable>(new Lease());
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoopAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// The apply command. In this build the gate is closed, so apply stops after the load, validation and hash checks, exits 2 and
/// never runs the applier (doc 45 section 1.7; the durable audit store is the Host adapter's).
/// </summary>
public sealed class ApplyCommandTests
{
    [Fact]
    public async Task Hash_mismatch_exits_invalid_input_before_the_gate_is_asked()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var gate = new CountingApplyGate(open: true);

        var run = await AdminRun.WithGate(gate, "setup", "apply", "--config", config, "--plan-hash", new string('0', 64));

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at planHash: does not match the plan; run the dry run again", run.Stdout);
        Assert.Equal(0, gate.RegistrationCount);
        Assert.Equal(0, gate.Lock.Acquisitions);
    }

    [Fact]
    public async Task Valid_plan_with_the_right_hash_exits_held_as_apply_unavailable_and_says_why()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Contains("Result: held, so nothing was changed (exit 2)", run.Stdout);
        Assert.Contains(AdminFixtures.ApplyUnavailableLine, run.Stdout);
        Assert.Contains("Plan hash: " + hash, run.Stdout);
        Assert.DoesNotContain("Applied steps", run.Stdout);
        Assert.DoesNotContain("no step implementation", run.All);
        Assert.Empty(run.Stderr);
    }

    [Fact]
    public async Task Closed_gate_never_runs_the_applier_even_with_a_plan_that_has_changes()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);
        var gate = new CountingApplyGate(open: false);

        var run = await AdminRun.WithGate(gate, "setup", "apply", "--config", config, "--plan-hash", hash);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Equal(0, gate.RegistrationCount);
        Assert.Equal(0, gate.Lock.Acquisitions);
    }

    [Fact]
    public async Task Open_gate_reaches_the_applier_so_the_closed_gate_test_is_a_real_proof()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);
        var gate = new CountingApplyGate(open: true);

        var run = await AdminRun.WithGate(gate, "setup", "apply", "--config", config, "--plan-hash", hash);

        // The applier takes the lock, then fails the changes with no steps (exit 3), and the probe counts the call.
        Assert.Equal((int)SetupExitCode.ApplyFailedRolledBack, run.ExitCode);
        Assert.Equal(1, gate.RegistrationCount);
        Assert.Equal(1, gate.Lock.Acquisitions);
        Assert.Contains("no step implementation for ", run.Stdout);
    }

    [Fact]
    public async Task Held_plan_with_the_right_hash_exits_held_before_the_gate_is_asked()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"));
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);
        var gate = new CountingApplyGate(open: true);

        var run = await AdminRun.WithGate(gate, "setup", "apply", "--config", config, "--plan-hash", hash);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Contains("Hold at ", run.Stdout);
        Assert.Contains("Result: held, so nothing was changed (exit 2)", run.Stdout);
        Assert.DoesNotContain(AdminFixtures.ApplyUnavailableLine, run.All);
        Assert.Equal(0, gate.RegistrationCount);
    }

    [Fact]
    public async Task Invalid_file_exits_invalid_input_before_the_hash_is_checked()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", new string('a', 64));

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at install.releaseChannel", run.Stdout);
    }

    [Fact]
    public async Task Json_unavailable_apply_reports_the_outcome_and_no_applied_steps()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash, "--json");

        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("apply", root.GetProperty("command").GetString());
        Assert.Equal((int)SetupExitCode.Held, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("held", root.GetProperty("result").GetString());
        Assert.Equal("apply_unavailable", root.GetProperty("outcome").GetString());
        Assert.Equal(0, root.GetProperty("appliedSteps").GetArrayLength());
        Assert.Equal(hash, root.GetProperty("planHash").GetString());
        Assert.Equal("audit", root.GetProperty("issues")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Unavailable_apply_output_shows_no_secret_reference_value()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);

        var text = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash);
        var json = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash, "--json");

        foreach (var run in new[] { text, json })
        {
            Assert.DoesNotContain("secret://bazaar/owner/app-id", run.All);
            Assert.DoesNotContain("kek.bin", run.All);
        }
    }
}
