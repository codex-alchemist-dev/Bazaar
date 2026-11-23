using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Kvm.Virsh;

namespace Bazaar.Server.Vms.Kvm.Tests;

/// <summary>Tests the virsh adapter against a fake runner and a fake file system. No process starts and no disk is touched.</summary>
public sealed class VirshKvmHostTests
{
    private static readonly string _vm = KvmNames.Domain(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private static readonly string _cp = KvmNames.Checkpoint(Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"));
    private static readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bazaar-kvm-vms"));
    private static readonly string _images = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bazaar-kvm-images"));
    private static readonly string[] _virsh = ["virsh", "--connect", "qemu:///system"];

    private readonly FakeRunner _runner = new();
    private readonly FakeFiles _files = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly VirshKvmHost _host;

    public VirshKvmHostTests()
    {
        _host = Create(new VirshKvmOptions(_root));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Virsh(params string[] rest) => [.. _virsh, .. rest];

    [Fact]
    public async Task Start_runs_virsh_start_with_the_connection_uri()
    {
        await _host.StartAsync(_vm, Ct);

        Assert.Equal(Virsh("start", _vm), Assert.Single(_runner.Calls));
    }

    [Fact]
    public async Task A_custom_connection_uri_is_used()
    {
        var host = Create(new VirshKvmOptions(_root, "qemu+ssh://host/system"));

        await host.StartAsync(_vm, Ct);

        Assert.Equal(["virsh", "--connect", "qemu+ssh://host/system", "start", _vm], Assert.Single(_runner.Calls));
    }

    [Fact]
    public async Task Save_checkpoint_and_restore_use_the_documented_verbs()
    {
        await _host.SaveAsync(_vm, Ct);
        await _host.CheckpointAsync(_vm, _cp, Ct);
        await _host.RestoreAsync(_vm, _cp, Ct);

        Assert.Equal(
            [Virsh("managedsave", _vm), Virsh("snapshot-create-as", _vm, _cp, "--atomic"), Virsh("managedsave-remove", _vm), Virsh("snapshot-revert", _vm, _cp)],
            _runner.Calls);
    }

    [Fact]
    public async Task Forced_stop_destroys_without_polling()
    {
        await _host.StopAsync(_vm, force: true, Ct);

        Assert.Equal(Virsh("destroy", _vm), Assert.Single(_runner.Calls));
    }

    [Fact]
    public async Task Graceful_stop_shuts_down_then_polls_until_shut_off()
    {
        _runner.Script("domstate", new KvmCommandResult(0, "running\n", ""), new KvmCommandResult(0, "in shutdown\n", ""), new KvmCommandResult(0, "shut off\n", ""));
        _runner.Script("dominfo", new KvmCommandResult(0, "Managed save: no\n", ""));

        await _host.StopAsync(_vm, force: false, Ct);

        Assert.Equal(Virsh("shutdown", _vm), _runner.Calls[0]);
        Assert.Equal(3, _runner.Calls.Count(c => c[3] == "domstate"));
        Assert.Equal(2, _delays.Count);
    }

    [Fact]
    public async Task Graceful_stop_throws_when_the_guest_never_powers_off()
    {
        var host = Create(new VirshKvmOptions(_root, ShutdownTimeout: TimeSpan.FromSeconds(6), ShutdownPollInterval: TimeSpan.FromSeconds(2)));
        _runner.Default("domstate", new KvmCommandResult(0, "running\n", ""));

        var ex = await Assert.ThrowsAsync<KvmCommandException>(() => host.StopAsync(_vm, force: false, Ct));

        Assert.Contains("did not shut down", ex.Message);
        Assert.Equal(3, _delays.Count);
    }

    [Fact]
    public async Task Create_backed_disk_runs_qemu_img_and_returns_the_child_path()
    {
        var parent = Path.Combine(_images, "golden.qcow2");
        _files.Existing.Add(parent);

        var child = await _host.CreateBackedDiskAsync(_vm, parent, Ct);

        var dir = Path.Combine(_root, _vm);
        Assert.Equal(Path.Combine(dir, "disk.qcow2"), child);
        Assert.Contains(dir, _files.Directories);
        Assert.Equal(["qemu-img", "create", "-f", "qcow2", "-b", parent, "-F", "qcow2", "--", child], Assert.Single(_runner.Calls));
    }

    [Fact]
    public async Task Create_backed_disk_refuses_a_missing_parent()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _host.CreateBackedDiskAsync(_vm, Path.Combine(_images, "none.qcow2"), Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Define_writes_the_xml_beside_the_disk_and_defines_that_file()
    {
        var disk = Path.Combine(_root, _vm, "disk.qcow2");

        await _host.DefineAsync(new KvmDomainDefinition(_vm, VmOs.Linux, 2, 4096, disk), Ct);

        var file = Path.Combine(_root, _vm, "domain.xml");
        Assert.Equal("<domain name='" + _vm + "'/>", _files.Written[file]);
        Assert.Equal(Virsh("define", file), Assert.Single(_runner.Calls));
    }

    [Theory]
    [InlineData("running\n", KvmDomainState.Running)]
    [InlineData("blocked\n", KvmDomainState.Running)]
    [InlineData("paused\n", KvmDomainState.Paused)]
    [InlineData("in shutdown\n", KvmDomainState.Transitioning)]
    [InlineData("crashed\n", KvmDomainState.Transitioning)]
    [InlineData("pmsuspended\n", KvmDomainState.Transitioning)]
    public async Task State_is_parsed_from_domstate(string output, KvmDomainState expected)
    {
        _runner.Default("domstate", new KvmCommandResult(0, output, ""));

        Assert.Equal(expected, await _host.GetStateAsync(_vm, Ct));
    }

    [Theory]
    [InlineData("Id:             -\nName:           x\nManaged save:   yes\n", KvmDomainState.Saved)]
    [InlineData("Id:             -\nManaged save:   no\n", KvmDomainState.Off)]
    [InlineData("Id:             -\n", KvmDomainState.Off)]
    public async Task Shut_off_is_saved_only_when_dominfo_reports_a_managed_save(string info, KvmDomainState expected)
    {
        _runner.Default("domstate", new KvmCommandResult(0, "shut off\n", ""));
        _runner.Default("dominfo", new KvmCommandResult(0, info, ""));

        Assert.Equal(expected, await _host.GetStateAsync(_vm, Ct));
        Assert.Equal(Virsh("dominfo", _vm), _runner.Calls[1]);
    }

    [Theory]
    [InlineData("error: failed to get domain 'bazaar-x'")]
    [InlineData("error: Domain not found: no domain with matching name")]
    public async Task A_missing_domain_is_reported_as_missing(string stderr)
    {
        _runner.Default("domstate", new KvmCommandResult(1, "", stderr));

        Assert.Equal(KvmDomainState.Missing, await _host.GetStateAsync(_vm, Ct));
    }

    [Fact]
    public async Task An_unknown_state_throws()
    {
        _runner.Default("domstate", new KvmCommandResult(0, "banana\n", ""));

        await Assert.ThrowsAsync<KvmCommandException>(() => _host.GetStateAsync(_vm, Ct));
    }

    [Fact]
    public async Task Another_domstate_failure_propagates_stderr()
    {
        _runner.Default("domstate", new KvmCommandResult(1, "", "error: failed to connect to the hypervisor"));

        var ex = await Assert.ThrowsAsync<KvmCommandException>(() => _host.GetStateAsync(_vm, Ct));

        Assert.Contains("failed to connect to the hypervisor", ex.Message);
    }

    [Fact]
    public async Task A_failing_verb_throws_with_its_stderr()
    {
        _runner.Default("start", new KvmCommandResult(1, "", "error: Requested operation is not valid"));

        var ex = await Assert.ThrowsAsync<KvmCommandException>(() => _host.StartAsync(_vm, Ct));

        Assert.Contains("virsh start", ex.Message);
        Assert.Contains("Requested operation is not valid", ex.Message);
    }

    [Fact]
    public async Task Removing_a_missing_domain_succeeds_and_still_deletes_the_folder()
    {
        _runner.Default("domstate", new KvmCommandResult(1, "", "error: failed to get domain"));

        await _host.RemoveAsync(_vm, deleteDisk: true, Ct);

        Assert.Equal(Virsh("domstate", _vm), Assert.Single(_runner.Calls));
        Assert.Equal([Path.Combine(_root, _vm)], _files.Deleted);
    }

    [Fact]
    public async Task Removing_a_running_domain_destroys_then_undefines()
    {
        _runner.Default("domstate", new KvmCommandResult(0, "running\n", ""));

        await _host.RemoveAsync(_vm, deleteDisk: true, Ct);

        Assert.Equal(
            [Virsh("domstate", _vm), Virsh("destroy", _vm), Virsh("undefine", _vm, "--managed-save", "--snapshots-metadata", "--nvram")],
            _runner.Calls);
        Assert.Equal([Path.Combine(_root, _vm)], _files.Deleted);
    }

    [Fact]
    public async Task Removing_a_shut_off_domain_keeps_the_disk_unless_asked()
    {
        _runner.Default("domstate", new KvmCommandResult(0, "shut off\n", ""));
        _runner.Default("dominfo", new KvmCommandResult(0, "Managed save: no\n", ""));

        await _host.RemoveAsync(_vm, deleteDisk: false, Ct);

        Assert.DoesNotContain(_runner.Calls, c => c.Contains("destroy"));
        Assert.Equal(Virsh("undefine", _vm, "--managed-save", "--snapshots-metadata", "--nvram"), _runner.Calls[^1]);
        Assert.Empty(_files.Deleted);
    }

    [Theory]
    [InlineData("; rm -rf /")]
    [InlineData("bazaar-11111111222233334444555555555555 extra")]
    [InlineData("-bazaar-11111111222233334444555555555555")]
    [InlineData("--help")]
    [InlineData("bazaar-cp-11111111222233334444555555555555")]
    [InlineData("bazaar-../../etc")]
    [InlineData("")]
    public async Task Bad_domain_names_are_refused_before_anything_runs(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StartAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StopAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StopAsync(name, false, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.SaveAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(name, _cp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(name, _cp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RemoveAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.GetStateAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateBackedDiskAsync(name, Path.Combine(_root, "p.qcow2"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.DefineAsync(new KvmDomainDefinition(name, VmOs.Linux, 1, 1024, Path.Combine(_root, "d.qcow2")), Ct));

        Assert.Empty(_runner.Calls);
        Assert.Empty(_files.Written);
    }

    [Theory]
    [InlineData("; rm -rf /")]
    [InlineData("-snap")]
    [InlineData("bazaar-11111111222233334444555555555555")]
    [InlineData("bazaar-cp-1 --disk-only")]
    public async Task Bad_checkpoint_names_are_refused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(_vm, name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(_vm, name, Ct));

        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("relative/parent.qcow2")]
    [InlineData("-b")]
    [InlineData("")]
    public async Task Bad_parent_paths_are_refused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateBackedDiskAsync(_vm, path, Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task A_parent_path_with_a_control_character_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateBackedDiskAsync(_vm, Path.Combine(_images, "a\nb.qcow2"), Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void Bad_options_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Create(new VirshKvmOptions("relative")));
        Assert.Throws<ArgumentException>(() => Create(new VirshKvmOptions(_root, "--help")));
    }

    [Fact]
    public async Task The_process_runner_refuses_other_programs()
    {
        var runner = new ProcessKvmCommandRunner();

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("sh", ["-c", "true"], null, Ct));
    }

    [Fact]
    public void The_process_runner_pins_the_C_locale_so_parsed_output_is_never_translated()
    {
        var info = ProcessKvmCommandRunner.CreateStartInfo("virsh", ["domstate", "bazaar-x"], redirectInput: false);

        Assert.Equal("C", info.Environment["LC_ALL"]);
        Assert.False(info.Environment.ContainsKey("LANGUAGE"));
        Assert.False(info.UseShellExecute);
        Assert.Equal<string>(["domstate", "bazaar-x"], info.ArgumentList);
    }

    private VirshKvmHost Create(VirshKvmOptions options) => new(
        _runner,
        options,
        _files,
        (interval, _) =>
        {
            _delays.Add(interval);
            return Task.CompletedTask;
        },
        domain => "<domain name='" + domain.Name + "'/>");

    private sealed class FakeFiles : IKvmFileSystem
    {
        public HashSet<string> Existing { get; } = [];

        public List<string> Directories { get; } = [];

        public Dictionary<string, string> Written { get; } = [];

        public List<string> Deleted { get; } = [];

        public bool FileExists(string path) => Existing.Contains(path);

        public void CreateDirectory(string path) => Directories.Add(path);

        public Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
        {
            Written[path] = contents;
            return Task.CompletedTask;
        }

        public void DeleteDirectory(string path) => Deleted.Add(path);

        public HashSet<string> Links { get; } = [];

        public List<string> DeletedLinks { get; } = [];

        public bool IsSymbolicLink(string path) => Links.Contains(path);

        public void DeleteLink(string path) => DeletedLinks.Add(path);
    }

    private sealed class FakeRunner : IKvmCommandRunner
    {
        private readonly Dictionary<string, Queue<KvmCommandResult>> _scripts = [];
        private readonly Dictionary<string, KvmCommandResult> _defaults = [];

        /// <summary>Each call as program followed by its arguments.</summary>
        public List<string[]> Calls { get; } = [];

        public void Script(string verb, params KvmCommandResult[] results) => _scripts[verb] = new Queue<KvmCommandResult>(results);

        public void Default(string verb, KvmCommandResult result) => _defaults[verb] = result;

        public Task<KvmCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
        {
            Calls.Add([program, .. arguments]);
            var verb = program == "virsh" ? arguments[2] : arguments[0];
            if (_scripts.TryGetValue(verb, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(_defaults.GetValueOrDefault(verb) ?? new KvmCommandResult(0, "", ""));
        }
    }
}
