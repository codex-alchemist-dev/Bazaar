using Bazaar.Vms.WindowsSandbox.WsbCli;

namespace Bazaar.Vms.WindowsSandbox.Tests;

/// <summary>Tests the <c>wsb.exe</c> adapter against a fake runner. No sandbox is started, so cloud CI stays clean.</summary>
public sealed class WsbCliSandboxHostTests : IDisposable
{
    private const string _configXml = "<Configuration><Networking>Disable</Networking><MemoryInMB>4096</MemoryInMB></Configuration>";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-wsb-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWsbCommandRunner _runner = new();
    private readonly WsbCliSandboxHost _host;

    public WsbCliSandboxHostTests()
    {
        _host = new WsbCliSandboxHost(_runner, _directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteConfig_writes_utf8_without_bom_and_returns_the_path()
    {
        var path = await _host.WriteConfigAsync("agent-1", _configXml, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_directory, "agent-1.wsb"), path);
        Assert.Equal(_configXml, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "config must not start with a BOM");
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData("name.with.dots")]
    public async Task WriteConfig_refuses_names_that_are_not_safe_file_names(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.WriteConfigAsync(name, _configXml, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Launch_reads_the_config_and_passes_its_xml_as_an_argument()
    {
        var path = await _host.WriteConfigAsync("agent-1", _configXml, TestContext.Current.CancellationToken);
        _runner.Enqueue(new WsbCommandResult(0, "{\n  \"Id\": \"abc-123\"\n}\n", ""));

        var id = await _host.LaunchAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("abc-123", id);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal<string>(["start", "-c", _configXml, "--raw"], call);
    }

    [Fact]
    public async Task Launch_fails_when_wsb_refuses_the_second_instance()
    {
        var path = await _host.WriteConfigAsync("agent-2", _configXml, TestContext.Current.CancellationToken);
        _runner.Enqueue(new WsbCommandResult(-2147221002, "", "Application cannot be run more than once (0x800401F6 (CO_E_APPSINGLEUSE))"));

        var ex = await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.LaunchAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("CO_E_APPSINGLEUSE", ex.Message);
    }

    [Fact]
    public async Task Launch_fails_when_start_prints_no_id()
    {
        var path = await _host.WriteConfigAsync("agent-3", _configXml, TestContext.Current.CancellationToken);
        _runner.Enqueue(new WsbCommandResult(0, "{}", ""));

        await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.LaunchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Launch_fails_on_output_that_is_not_json()
    {
        var path = await _host.WriteConfigAsync("agent-4", _configXml, TestContext.Current.CancellationToken);
        _runner.Enqueue(new WsbCommandResult(0, "started", ""));

        await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.LaunchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Shutdown_calls_stop_with_the_id_and_ignores_the_force_flag()
    {
        _runner.Enqueue(new WsbCommandResult(0, "{}", ""));
        _runner.Enqueue(new WsbCommandResult(0, "{}", ""));

        await _host.ShutdownAsync("abc-123", force: false, TestContext.Current.CancellationToken);
        await _host.ShutdownAsync("abc-123", force: true, TestContext.Current.CancellationToken);

        Assert.Equal<string>(["stop", "--id", "abc-123", "--raw"], _runner.Calls[0]);
        Assert.Equal<string>(["stop", "--id", "abc-123", "--raw"], _runner.Calls[1]);
    }

    [Fact]
    public async Task Shutdown_fails_when_stop_exits_non_zero()
    {
        _runner.Enqueue(new WsbCommandResult(1, "", "no such sandbox"));

        var ex = await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.ShutdownAsync("gone", force: true, TestContext.Current.CancellationToken));

        Assert.Contains("no such sandbox", ex.Message);
    }

    [Fact]
    public async Task Count_and_IsRunning_read_the_list()
    {
        _runner.Enqueue(new WsbCommandResult(0, "{\"WindowsSandboxEnvironments\":[{\"Id\":\"abc-123\"}]}", ""));
        _runner.Enqueue(new WsbCommandResult(0, "{\"WindowsSandboxEnvironments\":[{\"Id\":\"abc-123\"}]}", ""));
        _runner.Enqueue(new WsbCommandResult(0, "{\"WindowsSandboxEnvironments\":[]}", ""));

        Assert.Equal(1, await _host.CountRunningAsync(TestContext.Current.CancellationToken));
        Assert.True(await _host.IsRunningAsync("ABC-123", TestContext.Current.CancellationToken));
        Assert.False(await _host.IsRunningAsync("abc-123", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Count_is_zero_for_empty_list_output()
    {
        _runner.Enqueue(new WsbCommandResult(0, "", ""));

        Assert.Equal(0, await _host.CountRunningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Count_fails_when_list_exits_non_zero()
    {
        _runner.Enqueue(new WsbCommandResult(5, "", "access denied"));

        await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.CountRunningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteConfig_removes_the_file_and_tolerates_a_missing_one()
    {
        var path = await _host.WriteConfigAsync("agent-5", _configXml, TestContext.Current.CancellationToken);

        await _host.DeleteConfigAsync(path, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));

        await _host.DeleteConfigAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Live_round_trip_runs_only_when_explicitly_enabled()
    {
        // Real sandbox calls. Opt-in on the owner's Windows PC with BAZAAR_WSB_LIVE=1; cloud CI and other machines skip it.
        if (Environment.GetEnvironmentVariable("BAZAAR_WSB_LIVE") != "1")
        {
            Assert.Skip("Set BAZAAR_WSB_LIVE=1 on a Windows host with Windows Sandbox to run this.");
        }

        var live = new WsbCliSandboxHost(new ProcessWsbCommandRunner(), _directory);
        var path = await live.WriteConfigAsync("live-probe", _configXml, TestContext.Current.CancellationToken);
        var id = await live.LaunchAsync(path, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await live.IsRunningAsync(id, TestContext.Current.CancellationToken));
        }
        finally
        {
            await live.ShutdownAsync(id, force: true, TestContext.Current.CancellationToken);
            await live.DeleteConfigAsync(path, TestContext.Current.CancellationToken);
        }

        Assert.False(await live.IsRunningAsync(id, TestContext.Current.CancellationToken));
    }
}

/// <summary>Replays queued <c>wsb</c> results and records each argument list. Fails loudly when a call has no queued answer.</summary>
internal sealed class FakeWsbCommandRunner : IWsbCommandRunner
{
    private readonly Queue<WsbCommandResult> _results = new();

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public void Enqueue(WsbCommandResult result) => _results.Enqueue(result);

    public Task<WsbCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        Calls.Add(arguments.ToArray());
        if (!_results.TryDequeue(out var result))
        {
            throw new InvalidOperationException("No queued wsb result for: " + string.Join(' ', arguments));
        }

        return Task.FromResult(result);
    }
}
