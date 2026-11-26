using Bazaar.Server.Vms.HyperV.PowerShell;

namespace Bazaar.Server.Vms.HyperV.Tests;

/// <summary>Tests the PowerShell adapter against a fake runner. No process starts and no Hyper-V is touched.</summary>
public sealed class PowerShellHyperVHostTests : IDisposable
{
    private static readonly string _vm = HyperVNames.Vm(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-hyperv-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly PowerShellHyperVHost _host;

    public PowerShellHyperVHostTests()
    {
        _host = new PowerShellHyperVHost(_runner, Path.Combine(_root, "scripts"), Path.Combine(_root, "vms"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ScriptPath => Path.Combine(_root, "scripts", $"bazaar-hyperv-{HyperVScript.Hash()}.ps1");

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Every_call_runs_the_hashed_script_with_fixed_arguments()
    {
        await _host.StartAsync(_vm, Ct);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal<string>(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", ScriptPath, "-Operation", "Start", "-Name", _vm], call);
    }

    [Fact]
    public async Task The_script_is_written_once_and_replaced_when_it_was_changed_on_disk()
    {
        await _host.StartAsync(_vm, Ct);
        Assert.Equal(HyperVScript.Text, await File.ReadAllTextAsync(ScriptPath, Ct));

        await File.WriteAllTextAsync(ScriptPath, "Remove-Item C:\\ -Recurse", Ct);
        await _host.StartAsync(_vm, Ct);

        Assert.Equal(HyperVScript.Text, await File.ReadAllTextAsync(ScriptPath, Ct));
    }

    [Fact]
    public async Task Create_vm_passes_sizing_and_os_as_separate_arguments()
    {
        await _host.CreateVmAsync(new HyperVVmDefinition(_vm, Environments.VmOs.Linux, 4, 8192, @"C:\vms\disk.vhdx"), Ct);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal<string>(
            ["-Operation", "CreateVm", "-Name", _vm, "-DiskPath", @"C:\vms\disk.vhdx", "-GuestOs", "Linux", "-Vcpu", "4", "-MemoryMb", "8192"],
        var disk = Path.Combine(_root, "vms", _vm, "disk.vhdx");
        await _host.CreateVmAsync(new HyperVVmDefinition(_vm, Environments.VmOs.Linux, 4, 8192, disk), Ct);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal<string>(
            ["-Operation", "CreateVm", "-Name", _vm, "-DiskPath", disk, "-GuestOs", "Linux", "-Vcpu", "4", "-MemoryMb", "8192"],
            call.Skip(6).ToArray());
    }

    [Fact]
    public async Task Create_disk_puts_the_child_under_a_folder_named_for_the_vm()
    {
        var parent = OperatingSystem.IsWindows() ? @"C:\images\golden.vhdx" : "/images/golden.vhdx";

        var disk = await _host.CreateDifferencingDiskAsync(_vm, parent, Ct);

        Assert.Equal(Path.Combine(_root, "vms", _vm, "disk.vhdx"), disk);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal<string>(["-Operation", "CreateDisk", "-Name", _vm, "-ParentPath", parent, "-DiskPath", disk], call.Skip(6).ToArray());
    }

    [Theory]
    [InlineData("golden.vhdx")]
    [InlineData("/images/golden.vhd")]
    public async Task Create_disk_refuses_a_parent_that_is_not_a_full_vhdx_path(string parent)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateDifferencingDiskAsync(_vm, parent, Ct));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Stop_adds_the_force_switch_only_when_forced()
    {
        await _host.StopAsync(_vm, force: false, Ct);
        await _host.StopAsync(_vm, force: true, Ct);

        Assert.DoesNotContain("-Force", _runner.Calls[0]);
        Assert.Equal("-Force", _runner.Calls[1][^1]);
    }

    [Fact]
    public async Task Remove_asks_for_the_disk_to_go_only_when_told()
    {
        await _host.RemoveAsync(_vm, deleteDisk: false, Ct);
        await _host.RemoveAsync(_vm, deleteDisk: true, Ct);

        Assert.DoesNotContain("-DeleteDisk", _runner.Calls[0]);
        Assert.Contains("-DeleteDisk", _runner.Calls[1]);
    }

    [Theory]
    [InlineData("Running", HyperVVmState.Running)]
    [InlineData("Off", HyperVVmState.Off)]
    [InlineData("Saved", HyperVVmState.Saved)]
    [InlineData("Paused", HyperVVmState.Paused)]
    [InlineData("Missing", HyperVVmState.Missing)]
    [InlineData("Starting", HyperVVmState.Transitioning)]
    [InlineData("Saving", HyperVVmState.Transitioning)]
    public async Task State_maps_what_powershell_prints(string printed, HyperVVmState expected)
    {
        _runner.Enqueue(new HyperVCommandResult(0, printed + "\r\n", ""));

        Assert.Equal(expected, await _host.GetStateAsync(_vm, Ct));
    }

    [Fact]
    public async Task State_with_no_output_is_an_error_not_a_guess()
    {
        _runner.Enqueue(new HyperVCommandResult(0, "  \r\n", ""));

        await Assert.ThrowsAsync<HyperVCommandException>(() => _host.GetStateAsync(_vm, Ct));
    }

    [Fact]
    public async Task A_failed_call_reports_the_error_text_and_exit_code()
    {
        _runner.Enqueue(new HyperVCommandResult(1, "", "Start-VM : the VM is locked"));

        var ex = await Assert.ThrowsAsync<HyperVCommandException>(() => _host.StartAsync(_vm, Ct));

        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("locked", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("web-server")]
    [InlineData("bazaar-ABCDEF")]
    [InlineData("bazaar-11111111222233334444555555555555; calc")]
    public async Task Names_this_provider_did_not_create_never_reach_powershell(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StartAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.GetStateAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RemoveAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(name, HyperVNames.Checkpoint(Guid.NewGuid()), Ct));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task A_checkpoint_name_this_provider_did_not_create_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(_vm, "snap one", Ct));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void The_script_validates_names_and_accepts_only_the_listed_operations()
    {
        Assert.Contains("[ValidatePattern('^bazaar-[0-9a-f]{32}$')]", HyperVScript.Text, StringComparison.Ordinal);
        Assert.Contains("[ValidatePattern('^bazaar-cp-[0-9a-f]{32}$')]", HyperVScript.Text, StringComparison.Ordinal);
        Assert.Contains("[ValidatePattern('(?-i)^bazaar-[0-9a-f]{32}\\z')]", HyperVScript.Text, StringComparison.Ordinal);
        Assert.Contains("[ValidatePattern('(?-i)^bazaar-cp-[0-9a-f]{32}\\z')]", HyperVScript.Text, StringComparison.Ordinal);
        foreach (var operation in HyperVScript.Operations)
        {
            Assert.Contains($"'{operation}'", HyperVScript.Text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Invoke-Expression", HyperVScript.Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeRunner : IHyperVCommandRunner
    {
        private readonly Queue<HyperVCommandResult> _results = new();

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public void Enqueue(HyperVCommandResult result) => _results.Enqueue(result);

        public Task<HyperVCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new HyperVCommandResult(0, "", ""));
        }
    }
}
