using Bazaar.Server.Providers.Windows;

namespace Bazaar.Server.Tests.Providers.Windows;

public class WindowsServiceHostProviderTests
{
    private const string _restartPolicy = """
        [SC] QueryServiceConfig2 SUCCESS

        SERVICE_NAME: Bazaar.Server
                RESET_PERIOD (in seconds)    : 86400
                REBOOT_MESSAGE               :
                COMMAND_LINE                 :
                FAILURE_ACTIONS              : RESTART -- Delay = 60000 milliseconds.
                                               RESTART -- Delay = 60000 milliseconds.
                                               RESTART -- Delay = 300000 milliseconds.
        """;

    private const string _noPolicy = """
        [SC] QueryServiceConfig2 SUCCESS

        SERVICE_NAME: Bazaar.Server
                RESET_PERIOD (in seconds)    : 0
                REBOOT_MESSAGE               :
                COMMAND_LINE                 :
                FAILURE_ACTIONS              :
        """;

    private static FakeRunner CreateRunner(string output) => new() { Output = output };

    [Fact]
    public void Kind_is_windows_service()
    {
        Assert.Equal("windows_service", new WindowsServiceHostProvider().Kind);
    }

    [Fact]
    public void Probe_is_usable_only_on_windows_and_reports_why_not()
    {
        var status = new WindowsServiceHostProvider().Probe();

        Assert.Equal("windows_service", status.Kind);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(status.Usable);
            Assert.Null(status.Reason);
        }
        else
        {
            Assert.False(status.Usable);
            Assert.Equal("requires Windows", status.Reason);
        }
    }

    [Fact]
    public void Probe_runs_no_process_and_changes_no_state()
    {
        var runner = CreateRunner(_restartPolicy);
        var provider = new WindowsServiceHostProvider("Bazaar.Server", runner.Run);

        _ = provider.Probe();
        _ = provider.Probe();

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Recovery_reads_restart_actions_and_reset_period_from_sc_output()
    {
        var provider = new WindowsServiceHostProvider("Bazaar.Server", CreateRunner(_restartPolicy).Run);

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.True(report.Configured);
        Assert.Equal(
            ["restart after 60000 ms", "restart after 60000 ms", "restart after 300000 ms", "failure count resets after 86400 s"],
            report.Actions);
    }

    [Fact]
    public async Task Recovery_queries_qfailure_for_the_named_service()
    {
        var runner = CreateRunner(_restartPolicy);
        var provider = new WindowsServiceHostProvider("Bazaar.Server", runner.Run);

        _ = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("qfailure Bazaar.Server", call.Arguments);
        Assert.EndsWith("sc.exe", call.File, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recovery_reports_not_configured_when_no_failure_actions_are_set()
    {
        var provider = new WindowsServiceHostProvider("Bazaar.Server", CreateRunner(_noPolicy).Run);

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.False(report.Configured);
        Assert.Empty(report.Actions);
    }

    [Fact]
    public async Task Recovery_reports_not_configured_when_the_service_is_missing()
    {
        const string missing = "[SC] OpenService FAILED 1060:\r\n\r\nThe specified service does not exist as an installed service.\r\n";
        var provider = new WindowsServiceHostProvider("Bazaar.Server", CreateRunner(missing).Run);

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.False(report.Configured);
        Assert.Empty(report.Actions);
    }

    [Fact]
    public async Task Recovery_keeps_only_restart_entries_and_handles_crlf()
    {
        const string mixed = "SERVICE_NAME: Bazaar.Server\r\n" +
            "        RESET_PERIOD (in seconds)    : 0\r\n" +
            "        FAILURE_ACTIONS              : REBOOT -- Delay = 0 milliseconds.\r\n" +
            "                                       RESTART -- Delay = 5000 milliseconds.\r\n" +
            "                                       RUN PROCESS -- Delay = 0 milliseconds.\r\n";
        var provider = new WindowsServiceHostProvider("Bazaar.Server", CreateRunner(mixed).Run);

        var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);

        Assert.True(report.Configured);
        Assert.Equal(["restart after 5000 ms"], report.Actions);
    }

    [Fact]
    public async Task Recovery_on_the_default_runner_needs_windows()
    {
        var provider = new WindowsServiceHostProvider();

        if (OperatingSystem.IsWindows())
        {
            // The service may or may not be installed on this machine; the answer must be self-consistent either way.
            var report = await provider.RecoveryAsync(TestContext.Current.CancellationToken);
            Assert.Equal(report.Configured, report.Actions.Count > 0);
        }
        else
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => provider.RecoveryAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bazaar Server")]
    [InlineData("Bazaar;del")]
    [InlineData("Bazaar\"x")]
    public void Constructor_rejects_service_names_that_are_not_safe_on_a_command_line(string serviceName)
    {
        Assert.ThrowsAny<ArgumentException>(() => new WindowsServiceHostProvider(serviceName, CreateRunner(string.Empty).Run));
    }

    private sealed class FakeRunner
    {
        public List<(string File, string Arguments)> Calls { get; } = [];

        public string Output { get; init; } = string.Empty;

        public Task<string> Run(string file, string arguments, CancellationToken ct)
        {
            Calls.Add((file, arguments));
            return Task.FromResult(Output);
        }
    }
}
