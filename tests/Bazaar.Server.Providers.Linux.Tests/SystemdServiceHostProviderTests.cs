using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Linux;

namespace Bazaar.Server.Providers.Linux.Tests;

public class SystemdServiceHostProviderTests
{
    private const string _systemdDirectory = "/run/systemd/system";
    private const string _journalSocket = "/run/systemd/journal/socket";
    private const string _propertyList = "--property=Restart,RestartUSec,StartLimitIntervalUSec,StartLimitBurst";

    private static SystemdServiceHostProvider Create(
        bool isLinux,
        IEnumerable<string> existingPaths,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>>? runProcess = null)
    {
        var existing = existingPaths.ToHashSet(StringComparer.Ordinal);
        return new SystemdServiceHostProvider(
            () => isLinux,
            path => existing.Contains(path),
            runProcess ?? ((_, _, _) => throw new InvalidOperationException("The runner should not be called.")));
    }

    private static Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> Runner(string output, List<(string File, string[] Arguments)> calls) =>
        (file, args, _) =>
        {
            calls.Add((file, args.ToArray()));
            return Task.FromResult(output);
        };

    [Fact]
    public void Kind_is_systemd()
    {
        Assert.Equal("systemd", Create(true, []).Kind);
    }

    [Fact]
    public void Probe_is_unusable_outside_linux_and_names_the_requirement()
    {
        var status = Create(isLinux: false, [_systemdDirectory, _journalSocket]).Probe();

        Assert.False(status.Usable);
        Assert.Equal("requires Linux", status.Reason);
        Assert.Empty(status.Features);
    }

    [Fact]
    public void Probe_is_unusable_when_systemd_is_not_running()
    {
        var status = Create(isLinux: true, [_journalSocket]).Probe();

        Assert.False(status.Usable);
        Assert.Equal("systemd is not running", status.Reason);
        Assert.Empty(status.Features);
    }

    [Fact]
    public void Probe_is_usable_with_journald_when_systemd_and_the_journal_socket_exist()
    {
        var status = Create(isLinux: true, [_systemdDirectory, _journalSocket]).Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        var feature = Assert.Single(status.Features);
        Assert.Equal("journald", feature);
    }

    [Fact]
    public void Probe_is_usable_without_journald_when_the_journal_socket_is_absent()
    {
        var status = Create(isLinux: true, [_systemdDirectory]).Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        Assert.Empty(status.Features);
    }

    [Fact]
    public void Probe_does_not_run_any_process()
    {
        var calls = new List<(string File, string[] Arguments)>();
        var provider = Create(isLinux: true, [_systemdDirectory, _journalSocket], Runner(string.Empty, calls));

        _ = provider.Probe();

        Assert.Empty(calls);
    }

    [Fact]
    public void Probe_on_this_machine_matches_the_host_OS()
    {
        var status = new SystemdServiceHostProvider().Probe();

        if (OperatingSystem.IsLinux())
        {
            var systemdRunning = Directory.Exists(_systemdDirectory);
            Assert.Equal(systemdRunning, status.Usable);
            Assert.Equal(systemdRunning ? null : "systemd is not running", status.Reason);
        }
        else
        {
            Assert.False(status.Usable);
            Assert.Equal("requires Linux", status.Reason);
        }
    }

    [Fact]
    public async Task Recovery_reads_restart_on_failure_with_delay_and_start_limit()
    {
        var output = """
            Restart=on-failure
            RestartUSec=1min
            StartLimitIntervalUSec=10s
            StartLimitBurst=5
            """;
        var calls = new List<(string File, string[] Arguments)>();
        var provider = Create(true, [], Runner(output, calls));

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.True(report.Configured);
        var action = Assert.Single(report.Actions);
        Assert.Equal("restart on failure after 1min (at most 5 starts within 10s)", action);

        var call = Assert.Single(calls);
        Assert.Equal("systemctl", call.File);
        Assert.Equal(["show", "bazaar-server.service", _propertyList], call.Arguments);
    }

    [Fact]
    public async Task Recovery_reports_no_policy_when_restart_is_no()
    {
        var output = """
            Restart=no
            RestartUSec=100ms
            StartLimitIntervalUSec=10s
            StartLimitBurst=5
            """;
        var provider = Create(true, [], Runner(output, []));

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.False(report.Configured);
        Assert.Empty(report.Actions);
    }

    [Fact]
    public async Task Recovery_treats_missing_keys_as_no_policy()
    {
        var provider = Create(true, [], Runner("LoadState=loaded\n", []));

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.False(report.Configured);
        Assert.Empty(report.Actions);
    }

    [Fact]
    public async Task Recovery_without_a_delay_or_start_limit_still_describes_the_restart()
    {
        var provider = Create(true, [], Runner("Restart=always\n", []));

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.True(report.Configured);
        var action = Assert.Single(report.Actions);
        Assert.Equal("restart always", action);
    }

    [Fact]
    public async Task Recovery_converts_plain_microsecond_values_to_readable_time()
    {
        var output = "Restart=on-abnormal\nRestartUSec=60000000\nStartLimitBurst=3\nStartLimitIntervalUSec=300000000\n";
        var provider = Create(true, [], Runner(output, []));

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        var action = Assert.Single(report.Actions);
        Assert.Equal("restart on abnormal exit after 1min (at most 3 starts within 5min)", action);
    }

    [Fact]
    public async Task Recovery_uses_the_configured_unit_name()
    {
        var calls = new List<(string File, string[] Arguments)>();
        var provider = new SystemdServiceHostProvider(
            () => true,
            _ => false,
            Runner("Restart=no\n", calls),
            "custom.service");

        await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        var call = Assert.Single(calls);
        Assert.Equal("custom.service", call.Arguments[1]);
    }

    [Fact]
    public async Task Unit_status_reads_installed_running_and_enabled_from_one_systemctl_call()
    {
        var output = """
            LoadState=loaded
            ActiveState=active
            UnitFileState=enabled
            """;
        var calls = new List<(string File, string[] Arguments)>();
        var provider = Create(true, [_systemdDirectory], Runner(output, calls));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ServiceUnitStatus("systemd", true, true, true), status);
        var call = Assert.Single(calls);
        Assert.Equal("systemctl", call.File);
        Assert.Equal(
            ["show", "bazaar-server.service", "--property=LoadState,ActiveState,UnitFileState"],
            call.Arguments);
    }

    [Fact]
    public async Task Unit_status_reports_installed_but_stopped_and_disabled()
    {
        var output = "LoadState=loaded\nActiveState=inactive\nUnitFileState=disabled\n";
        var provider = Create(true, [_systemdDirectory], Runner(output, []));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ServiceUnitStatus("systemd", true, false, false), status);
    }

    [Fact]
    public async Task Unit_status_reports_a_failed_unit_as_installed_but_not_running()
    {
        var output = "LoadState=loaded\nActiveState=failed\nUnitFileState=enabled\n";
        var provider = Create(true, [_systemdDirectory], Runner(output, []));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ServiceUnitStatus("systemd", true, false, true), status);
    }

    [Fact]
    public async Task Unit_status_reports_nothing_installed_when_systemd_does_not_know_the_unit()
    {
        // systemctl show prints not-found for an unknown unit and exits 0.
        var output = "LoadState=not-found\nActiveState=inactive\nUnitFileState=\n";
        var provider = Create(true, [_systemdDirectory], Runner(output, []));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ServiceUnitStatus("systemd", false, false, false), status);
    }

    [Fact]
    public async Task Unit_status_is_none_without_running_anything_when_systemd_is_not_running()
    {
        var calls = new List<(string File, string[] Arguments)>();
        var provider = Create(true, [_journalSocket], Runner("LoadState=loaded\n", calls));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceUnitStatus.NoManager, status);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Unit_status_is_none_outside_linux()
    {
        var calls = new List<(string File, string[] Arguments)>();
        var provider = Create(false, [_systemdDirectory], Runner("LoadState=loaded\n", calls));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("none", status.Manager);
        Assert.False(status.Installed);
        Assert.False(status.Running);
        Assert.False(status.Enabled);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Unit_status_is_none_when_systemctl_is_missing_or_fails()
    {
        var provider = Create(
            true,
            [_systemdDirectory],
            (_, _, _) => throw new InvalidOperationException("'systemctl' exited with code 1: no bus"));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceUnitStatus.NoManager, status);
    }

    [Fact]
    public async Task Unit_status_is_none_when_the_systemctl_binary_does_not_start()
    {
        var provider = Create(
            true,
            [_systemdDirectory],
            (_, _, _) => throw new FileNotFoundException("systemctl was not found"));

        var status = await provider.UnitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("none", status.Manager);
    }

    [Fact]
    public async Task Unit_status_does_not_swallow_timeouts()
    {
        var provider = Create(
            true,
            [_systemdDirectory],
            (_, _, _) => throw new TimeoutException("'systemctl' did not finish within 15 seconds."));

        await Assert.ThrowsAsync<TimeoutException>(
            () => provider.UnitStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_propagates_runner_failures()
    {
        var provider = Create(
            true,
            [],
            (_, _, _) => throw new InvalidOperationException("'systemctl' exited with code 1: no such unit"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RecoveryAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no such unit", ex.Message);
    }

    [Fact]
    public void Constructor_rejects_a_unit_name_that_looks_like_an_option()
    {
        Assert.Throws<ArgumentException>(
            () => new SystemdServiceHostProvider(() => true, _ => false, Runner(string.Empty, []), "--user"));
    }
}
