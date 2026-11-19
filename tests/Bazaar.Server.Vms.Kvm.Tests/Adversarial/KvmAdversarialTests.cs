using System.Diagnostics;
using System.Xml.Linq;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Kvm.Virsh;

namespace Bazaar.Server.Vms.Kvm.Tests.Adversarial;

/// <summary>Hostile input against the virsh adapter, the domain XML builder and the process runner.</summary>
public sealed class KvmAdversarialTests
{
    private const string _good = "bazaar-11111111222233334444555555555555";
    private const string _goodCp = "bazaar-cp-66666666777788889999aaaaaaaaaaaa";
    private const string _uri = "qemu:///system";

    private static readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bazaar-kvm-adv-vms"));
    private static readonly string _images = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bazaar-kvm-adv-images"));

    private static readonly string[] _forbiddenElements =
        ["interface", "graphics", "hostdev", "filesystem", "channel", "serial", "console", "redirdev", "smartcard", "sound", "video", "input", "tpm", "parallel", "watchdog", "panic", "shmem"];

    private static readonly string[] _allowedDevices = ["disk", "vsock", "memballoon", "controller", "rng"];

    private readonly RecordingRunner _runner = new();
    private readonly RecordingFiles _files = new();
    private readonly VirshKvmHost _host;

    public KvmAdversarialTests()
    {
        _host = Create(new VirshKvmOptions(_root));
    }

    public static TheoryData<string> HostileDomains() =>
    [
        "--help",
        "bazaar-x;id",
        "-bazaar-11111111222233334444555555555555",
        _good + "\n",
        "\n" + _good,
        _good + "\r\nstart other",
        "bazaar-1111111122223333444455555555555",
        "bazaar-111111112222333344445555555555555",
        "BAZAAR-11111111222233334444555555555555",
        "bazaar-1111111122223333444455555555555A",
        "bazaar-../../etc/11111111222233334444555",
        _good + "/..",
        "bazaar-١١١١١١١١١١١١١١١١١١١١١١١١١١١١١١١١",
        "",
    ];

    public static TheoryData<string> HostileCheckpoints() =>
    [
        "--help",
        "-bazaar-cp-66666666777788889999aaaaaaaaaaaa",
        "bazaar-cp-x;id",
        _goodCp + "\n",
        "bazaar-cp-66666666777788889999aaaaaaaaaaa",
        "bazaar-cp-66666666777788889999aaaaaaaaaaaaa",
        "bazaar-cp-66666666777788889999AAAAAAAAAAAA",
        _good,
        "",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(HostileDomains))]
    public async Task A_hostile_domain_name_never_reaches_a_process_or_the_file_system(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StartAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StopAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StopAsync(name, false, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.SaveAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(name, _goodCp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(name, _goodCp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RemoveAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.GetStateAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateBackedDiskAsync(name, Path.Combine(_images, "p.qcow2"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _host.DefineAsync(new KvmDomainDefinition(name, VmOs.Linux, 1, 1024, Path.Combine(_root, name, "disk.qcow2")), Ct));

        Assert.Empty(_runner.Calls);
        Assert.Empty(_files.Touched);
    }

    [Theory]
    [MemberData(nameof(HostileCheckpoints))]
    public async Task A_hostile_checkpoint_name_never_reaches_a_process(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(_good, name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(_good, name, Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void The_name_patterns_refuse_a_trailing_newline()
    {
        Assert.False(KvmNames.IsDomainName(_good + "\n"));
        Assert.False(KvmNames.IsCheckpointName(_goodCp + "\n"));
        Assert.True(KvmNames.IsDomainName(_good));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("-c")]
    [InlineData("qemu:///system --readonly")]
    [InlineData("qemu:///sys\ttem")]
    [InlineData("qemu:///system\n")]
    [InlineData("qemu:///sys\u0000tem")]
    [InlineData("qemu:///sys\u001btem")]
    public void A_hostile_connection_uri_is_refused(string uri)
    {
        Assert.Throws<ArgumentException>(() => Create(new VirshKvmOptions(_root, uri)));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("-root")]
    [InlineData("")]
    [InlineData("/tmp/a\nb")]
    public void A_hostile_vm_directory_is_refused(string directory)
    {
        Assert.Throws<ArgumentException>(() => Create(new VirshKvmOptions(directory)));
    }

    [Theory]
    [InlineData("<other>/disk.qcow2")]
    [InlineData("<root>/disk.qcow2")]
    [InlineData("<root>/bazaar-22222222222222222222222222222222/disk.qcow2")]
    [InlineData("<own>/../bazaar-22222222222222222222222222222222/disk.qcow2")]
    [InlineData("<own>/../../etc/passwd")]
    [InlineData("<own>/other.qcow2")]
    [InlineData("<own>/sub/disk.qcow2")]
    [InlineData("<own>/disk.qcow2.bak")]
    [InlineData("<own>/Disk.qcow2")]
    [InlineData("-disk.qcow2")]
    [InlineData("-/tmp/x/disk.qcow2")]
    [InlineData("disk.qcow2")]
    public async Task A_disk_path_other_than_the_domains_own_is_refused(string template)
    {
        var disk = template
            .Replace("<own>", Path.Combine(_root, _good), StringComparison.Ordinal)
            .Replace("<root>", _root, StringComparison.Ordinal)
            .Replace("<other>", Path.Combine(Path.GetTempPath(), "elsewhere"), StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.DefineAsync(new KvmDomainDefinition(_good, VmOs.Linux, 1, 1024, disk), Ct));

        Assert.Empty(_runner.Calls);
        Assert.Empty(_files.Written);
    }

    [Fact]
    public async Task The_domains_own_disk_is_accepted()
    {
        await _host.DefineAsync(new KvmDomainDefinition(_good, VmOs.Linux, 1, 1024, Path.Combine(_root, _good, "disk.qcow2")), Ct);

        Assert.Equal(["virsh", "--connect", _uri, "define", Path.Combine(_root, _good, "domain.xml")], Assert.Single(_runner.Calls));
    }

    [Theory]
    [InlineData("<images>/golden.qcow2", true)]
    [InlineData("<root>/golden.qcow2", false)]
    [InlineData("<root>/bazaar-22222222222222222222222222222222/disk.qcow2", false)]
    [InlineData("<root>/x/../bazaar-22222222222222222222222222222222/disk.qcow2", false)]
    [InlineData("<images>/../bazaar-kvm-adv-vms/bazaar-22222222222222222222222222222222/disk.qcow2", false)]
    [InlineData("<root>-evil/golden.qcow2", true)]
    public async Task A_parent_inside_the_vm_directory_is_refused(string template, bool allowed)
    {
        var parent = template.Replace("<root>", _root, StringComparison.Ordinal).Replace("<images>", _images, StringComparison.Ordinal);
        _files.Existing.Add(Path.GetFullPath(parent));
        _files.Existing.Add(parent);

        if (allowed)
        {
            await _host.CreateBackedDiskAsync(_good, parent, Ct);
            Assert.Single(_runner.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateBackedDiskAsync(_good, parent, Ct));
            Assert.Empty(_runner.Calls);
        }
    }

    [Theory]
    [InlineData("-b")]
    [InlineData("relative.qcow2")]
    [InlineData("/images/a\nb.qcow2")]
    public async Task A_hostile_parent_path_is_refused(string parent)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateBackedDiskAsync(_good, parent, Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task A_symlinked_domain_directory_is_refused_for_disk_creation_and_define()
    {
        var directory = Path.Combine(_root, _good);
        _files.Links.Add(directory);
        var parent = Path.Combine(_images, "golden.qcow2");
        _files.Existing.Add(parent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.CreateBackedDiskAsync(_good, parent, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _host.DefineAsync(new KvmDomainDefinition(_good, VmOs.Linux, 1, 1024, Path.Combine(directory, "disk.qcow2")), Ct));

        Assert.Empty(_runner.Calls);
        Assert.Empty(_files.Written);
        Assert.DoesNotContain(directory, _files.Created);
    }

    [Fact]
    public async Task Removing_a_symlinked_domain_directory_deletes_the_link_and_never_follows_it()
    {
        var directory = Path.Combine(_root, _good);
        _files.Links.Add(directory);
        _runner.Fail("domstate", "error: failed to get domain");

        await _host.RemoveAsync(_good, deleteDisk: true, Ct);

        Assert.Equal([directory], _files.DeletedLinks);
        Assert.Empty(_files.DeletedDirectories);
    }

    [Fact]
    public void The_physical_file_system_unlinks_a_symlink_without_touching_its_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs a privilege on Windows.
        }

        var temp = Path.Combine(Path.GetTempPath(), "bazaar-kvm-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(temp, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious"), "keep");
        var link = Path.Combine(temp, "link");
        Directory.CreateSymbolicLink(link, target);
        try
        {
            var fs = new PhysicalKvmFileSystem();
            Assert.True(fs.IsSymbolicLink(link));
            Assert.False(fs.IsSymbolicLink(target));
            Assert.False(fs.IsSymbolicLink(Path.Combine(temp, "missing")));

            fs.DeleteDirectory(link);

            Assert.False(Directory.Exists(link));
            Assert.True(File.Exists(Path.Combine(target, "precious")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task Virsh_is_always_called_with_connect_uri_first_and_the_verb_second()
    {
        var host = Create(new VirshKvmOptions(_root, "qemu+ssh://host/system"));
        _runner.Default("domstate", new KvmCommandResult(0, "shut off\n", ""));
        _runner.Default("dominfo", new KvmCommandResult(0, "Managed save: no\n", ""));
        var disk = Path.Combine(_root, _good, "disk.qcow2");
        _files.Existing.Add(Path.Combine(_images, "p.qcow2"));

        await host.StartAsync(_good, Ct);
        await host.StopAsync(_good, force: true, Ct);
        await host.StopAsync(_good, force: false, Ct);
        await host.SaveAsync(_good, Ct);
        await host.CheckpointAsync(_good, _goodCp, Ct);
        await host.RestoreAsync(_good, _goodCp, Ct);
        await host.GetStateAsync(_good, Ct);
        await host.RemoveAsync(_good, deleteDisk: false, Ct);
        await host.DefineAsync(new KvmDomainDefinition(_good, VmOs.Linux, 1, 1024, disk), Ct);
        await host.CreateBackedDiskAsync(_good, Path.Combine(_images, "p.qcow2"), Ct);

        var virsh = _runner.Calls.Where(c => c[0] == "virsh").ToList();
        Assert.True(virsh.Count >= 10);
        string[] verbs = ["start", "destroy", "shutdown", "domstate", "managedsave", "managedsave-remove", "snapshot-create-as", "snapshot-revert", "dominfo", "undefine", "define"];
        foreach (var call in virsh)
        {
            Assert.Equal("--connect", call[1]);
            Assert.Equal("qemu+ssh://host/system", call[2]);
            Assert.Contains(call[3], verbs);
            Assert.DoesNotContain(call.Skip(4), argument => argument.StartsWith('-') && argument is not ("--atomic" or "--managed-save" or "--snapshots-metadata" or "--nvram"));
        }

        var qemu = Assert.Single(_runner.Calls, c => c[0] == "qemu-img");
        Assert.Equal("--", qemu[^2]);
    }

    [Fact]
    public void The_domain_has_exactly_the_allowed_devices_and_no_forbidden_element_anywhere()
    {
        var root = XDocument.Parse(KvmDomainXml.Build(Definition("/var/lib/x/disk.qcow2"))).Root!;
        var devices = root.Element("devices")!;

        Assert.Equal(_allowedDevices.Order(StringComparer.Ordinal), devices.Elements().Select(e => e.Name.LocalName).Order(StringComparer.Ordinal));
        foreach (var forbidden in _forbiddenElements)
        {
            Assert.Empty(root.Descendants(forbidden));
        }

        var usb = Assert.Single(devices.Elements("controller"));
        Assert.Equal("usb", (string?)usb.Attribute("type"));
        Assert.Equal("none", (string?)usb.Attribute("model"));
        Assert.NotNull(root.Element("memoryBacking")!.Element("nosharepages"));
        Assert.Equal("/dev/urandom", devices.Element("rng")!.Element("backend")!.Value);
        Assert.Null(root.Element("seclabel"));
    }

    [Theory]
    [InlineData("/srv/a\"b.qcow2")]
    [InlineData("/srv/a<b>.qcow2")]
    [InlineData("/srv/a&b.qcow2")]
    [InlineData("/srv/a'b.qcow2")]
    [InlineData("/srv/a&amp;b.qcow2")]
    [InlineData("/srv/\"/><interface type=\"network\"><source network=\"default\"/></interface><x a=\".qcow2")]
    [InlineData("/srv/]]></domain><domain>.qcow2")]
    public void Special_characters_in_the_disk_path_are_escaped_and_round_trip(string disk)
    {
        var xml = KvmDomainXml.Build(Definition(disk));
        var root = XDocument.Parse(xml).Root!;

        Assert.Equal(disk, (string?)root.Element("devices")!.Element("disk")!.Element("source")!.Attribute("file"));
        Assert.Empty(root.Descendants("interface"));
        Assert.Single(root.Descendants("disk"));
        Assert.DoesNotContain("<interface", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_process_runner_passes_arguments_as_a_list_and_forces_the_c_locale()
    {
        var hostile = new[] { "--connect", "qemu:///system", "start", "a b; id", "$(calc)", "`x`", "line\nbreak" };

        var info = ProcessKvmCommandRunner.CreateStartInfo("virsh", hostile, redirectInput: false);

        Assert.Equal(hostile, info.ArgumentList);
        Assert.Equal(string.Empty, info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.Equal("C", info.Environment["LC_ALL"]);
        Assert.False(info.Environment.ContainsKey("LANGUAGE"));
    }

    [Fact]
    public async Task Output_is_capped_and_the_cap_is_reported_once()
    {
        var exceeded = 0;
        using var reader = new StringReader(new string('x', 100_000));

        var text = await ProcessKvmCommandRunner.ReadCappedAsync(reader, 5000, () => exceeded++);

        Assert.Equal(5000, text.Length);
        Assert.Equal(1, exceeded);
    }

    [Fact]
    public async Task Output_under_the_cap_is_kept_whole()
    {
        var exceeded = 0;
        using var reader = new StringReader(new string('x', 5000));

        var text = await ProcessKvmCommandRunner.ReadCappedAsync(reader, 5000, () => exceeded++);

        Assert.Equal(5000, text.Length);
        Assert.Equal(0, exceeded);
    }

    [Fact]
    public async Task A_process_that_floods_its_output_is_stopped_and_reported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ProcessKvmCommandRunner(TimeSpan.FromSeconds(30), ["sh"], 1000);

        var ex = await Assert.ThrowsAsync<KvmCommandException>(() => runner.RunAsync("sh", ["-c", "while true; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; done"], null, Ct));

        Assert.Contains("more than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_that_never_ends_is_killed_with_its_children_at_the_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ProcessKvmCommandRunner(TimeSpan.FromMilliseconds(500), ["sh"], 1000);
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<KvmCommandException>(() => runner.RunAsync("sh", ["-c", "sleep 60 & wait"], null, Ct));

        Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task The_process_runner_sets_the_c_locale_for_the_child()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ProcessKvmCommandRunner(TimeSpan.FromSeconds(30), ["sh"], 1000);

        var result = await runner.RunAsync("sh", ["-c", "printf %s \"$LC_ALL\""], null, Ct);

        Assert.Equal("C", result.StdOut);
    }

    private static KvmDomainDefinition Definition(string disk) => new(_good, VmOs.Linux, 2, 2048, disk);

    private VirshKvmHost Create(VirshKvmOptions options) =>
        new(_runner, options, _files, (_, _) => Task.CompletedTask, domain => "<domain name='" + domain.Name + "'/>");

    private sealed class RecordingFiles : IKvmFileSystem
    {
        public HashSet<string> Existing { get; } = [];

        public HashSet<string> Links { get; } = [];

        public List<string> Created { get; } = [];

        public Dictionary<string, string> Written { get; } = [];

        public List<string> DeletedDirectories { get; } = [];

        public List<string> DeletedLinks { get; } = [];

        public IEnumerable<string> Touched => Created.Concat(Written.Keys).Concat(DeletedDirectories).Concat(DeletedLinks);

        public bool FileExists(string path) => Existing.Contains(path);

        public void CreateDirectory(string path) => Created.Add(path);

        public Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
        {
            Written[path] = contents;
            return Task.CompletedTask;
        }

        public void DeleteDirectory(string path) => DeletedDirectories.Add(path);

        public bool IsSymbolicLink(string path) => Links.Contains(path);

        public void DeleteLink(string path) => DeletedLinks.Add(path);
    }

    private sealed class RecordingRunner : IKvmCommandRunner
    {
        private readonly Dictionary<string, KvmCommandResult> _defaults = [];

        /// <summary>Each call as program followed by its arguments.</summary>
        public List<string[]> Calls { get; } = [];

        public void Default(string verb, KvmCommandResult result) => _defaults[verb] = result;

        public void Fail(string verb, string stderr) => _defaults[verb] = new KvmCommandResult(1, "", stderr);

        public Task<KvmCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
        {
            Calls.Add([program, .. arguments]);
            var verb = program == "virsh" ? arguments[2] : arguments[0];
            return Task.FromResult(_defaults.GetValueOrDefault(verb) ?? new KvmCommandResult(0, "", ""));
        }
    }
}
