using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.HyperV.PowerShell;

namespace Bazaar.Server.Vms.HyperV.Tests.Adversarial;

/// <summary>
/// Hostile input against the PowerShell adapter. Every refusal is asserted before the runner is called, so a bad value never reaches
/// <c>powershell.exe</c>, and the script text is pinned for the attack surface it removes.
/// </summary>
public sealed class HyperVAdversarialTests : IDisposable
{
    private const string _good = "bazaar-11111111222233334444555555555555";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-hyperv-adv-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingRunner _runner = new();
    private readonly PowerShellHyperVHost _host;

    public HyperVAdversarialTests()
    {
        _host = new PowerShellHyperVHost(_runner, Path.Combine(_root, "scripts"), Path.Combine(_root, "vms"));
    }

    public static TheoryData<string> HostileNames() =>
    [
        "bazaar-x'; Remove-Item C:\\ -Recurse; '",
        "$(calc)",
        "bazaar-`11111111222233334444555555555555",
        _good + "\n",
        _good + "\r\n",
        "\n" + _good,
        "bazaar-../../11111111222233334444555555555",
        "bazaar-11111111222233334444555555555555/..",
        "bazaar-1111111122223333444455555555555A",
        "BAZAAR-11111111222233334444555555555555",
        "bazaar-1111111122223333444455555555555",
        "bazaar-111111112222333344445555555555555",
        "bazaar-\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661",
        "bazaar-\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11\uff11",
        "-Operation",
        "",
    ];

    public static TheoryData<string> HostileCheckpoints() =>
    [
        "bazaar-cp-x'; Remove-Item C:\\ -Recurse; '",
        "$(calc)",
        "bazaar-cp-`11111111222233334444555555555555",
        "bazaar-cp-11111111222233334444555555555555\n",
        "bazaar-cp-..\\..\\11111111222233334444555555",
        "bazaar-cp-1111111122223333444455555555555A",
        "bazaar-cp-1111111122223333444455555555555",
        "bazaar-cp-111111112222333344445555555555555",
        "bazaar-cp-\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661\u0661",
        _good,
        "",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task A_hostile_vm_name_never_reaches_the_runner(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StartAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.StopAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.SaveAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.GetStateAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RemoveAsync(name, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(name, HyperVNames.Checkpoint(Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(name, HyperVNames.Checkpoint(Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateDifferencingDiskAsync(name, ParentDisk(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _host.CreateVmAsync(new HyperVVmDefinition(name, VmOs.Linux, 2, 2048, Path.Combine(_root, "vms", name, "disk.vhdx")), Ct));

        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(_root, "vms")));
    }

    [Theory]
    [MemberData(nameof(HostileCheckpoints))]
    public async Task A_hostile_checkpoint_name_never_reaches_the_runner(string checkpoint)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CheckpointAsync(_good, checkpoint, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RestoreAsync(_good, checkpoint, Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void The_name_patterns_refuse_a_trailing_newline()
    {
        Assert.False(HyperVNames.IsVmName(_good + "\n"));
        Assert.False(HyperVNames.IsCheckpointName("bazaar-cp-11111111222233334444555555555555\n"));
        Assert.True(HyperVNames.IsVmName(_good));
    }

    [Theory]
    [InlineData(@"..\other\disk.vhdx")]
    [InlineData("<vm>/../../other/disk.vhdx")]
    [InlineData("<vms>/disk.vhdx")]
    [InlineData("<vms>/bazaar-22222222222222222222222222222222/disk.vhdx")]
    [InlineData("<vm>/disk.vhd")]
    [InlineData("<vm>/disk.vhdx.exe")]
    [InlineData("<vm>/other.vhdx")]
    [InlineData("<vm>/*.vhdx")]
    [InlineData("<vm>/dis?.vhdx")]
    [InlineData("<vm>/dis[k].vhdx")]
    [InlineData("<vm>/disk.vhdx\n")]
    [InlineData(@"\\evil\share\bazaar-11111111222233334444555555555555\disk.vhdx")]
    [InlineData("//evil/share/bazaar-11111111222233334444555555555555/disk.vhdx")]
    [InlineData("disk.vhdx")]
    [InlineData("")]
    public async Task A_disk_path_outside_the_vm_directory_is_refused(string template)
    {
        var disk = template
            .Replace("<vm>", Path.Combine(_root, "vms", _good), StringComparison.Ordinal)
            .Replace("<vms>", Path.Combine(_root, "vms"), StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateVmAsync(new HyperVVmDefinition(_good, VmOs.Linux, 2, 2048, disk), Ct));

        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task The_vm_own_disk_path_is_accepted_with_any_case_and_dot_segments()
    {
        var own = Path.Combine(_root, "vms", _good, "disk.vhdx");
        var dotted = Path.Combine(_root, "vms", "x", "..", _good, ".", "disk.vhdx");

        await _host.CreateVmAsync(new HyperVVmDefinition(_good, VmOs.Linux, 2, 2048, own), Ct);
        await _host.CreateVmAsync(new HyperVVmDefinition(_good, VmOs.Linux, 2, 2048, dotted), Ct);

        Assert.Equal(2, _runner.Calls.Count);
    }

    [Theory]
    [InlineData("relative.vhdx")]
    [InlineData("/images/../etc/golden.vhdx")]
    [InlineData("/images/golden.vhd")]
    [InlineData("/images/golden.vhdx.txt")]
    [InlineData("/images/*.vhdx")]
    [InlineData("/images/gold?n.vhdx")]
    [InlineData("/images/gold[e]n.vhdx")]
    [InlineData("/images/golden.vhdx\n")]
    [InlineData("/images/gold\nen.vhdx")]
    [InlineData("//evil/share/golden.vhdx")]
    [InlineData("/images/golden|calc.vhdx")]
    [InlineData("")]
    public async Task A_parent_path_must_be_an_absolute_local_vhdx(string parent)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CreateDifferencingDiskAsync(_good, parent, Ct));

        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(_root, "vms", _good)));
    }

    [Fact]
    public async Task Arguments_are_separate_list_items_even_when_they_contain_spaces()
    {
        var parent = Path.Combine(_root, "golden images", "base image.vhdx");

        var disk = await _host.CreateDifferencingDiskAsync(_good, parent, Ct);

        var call = Assert.Single(_runner.Calls);
        var tail = call.Skip(6).ToArray();
        Assert.Equal<string>(["-Operation", "CreateDisk", "-Name", _good, "-ParentPath", parent, "-DiskPath", disk], tail);
        Assert.Contains(parent, call);
        Assert.DoesNotContain(call, argument => argument.Contains("-Operation", StringComparison.Ordinal) && argument.Length > "-Operation".Length);
    }

    [Fact]
    public async Task Remove_only_ever_names_the_disk_under_the_vm_directory()
    {
        await _host.RemoveAsync(_good, deleteDisk: true, Ct);

        var call = Assert.Single(_runner.Calls);
        var index = call.ToList().IndexOf("-DiskPath");
        Assert.Equal(Path.Combine(_root, "vms", _good, "disk.vhdx"), call[index + 1]);
    }

    [Fact]
    public void The_script_confines_parameters_and_deletion()
    {
        var text = HyperVScript.Text;

        Assert.Equal(2, CountOf(text, @"\.vhdx\z')][string]$"));
        Assert.Contains("[string]$ParentPath", text, StringComparison.Ordinal);
        Assert.Contains(@"[^*?\[\]<>|""\x00-\x1f]", text, StringComparison.Ordinal);
        Assert.Contains("(?-i)", text, StringComparison.Ordinal);
        Assert.Contains("Test-OwnedDisk -Path $DiskPath -VmName $Name -Standard", text, StringComparison.Ordinal);
        Assert.Contains("Test-OwnedDisk -Path $path -VmName $Name", text, StringComparison.Ordinal);
        Assert.Contains("'disk.vhdx'", text, StringComparison.Ordinal);

        // Every path cmdlet that touches a user-influenced path is literal.
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains("Remove-Item", StringComparison.Ordinal) || line.Contains("Test-Path", StringComparison.Ordinal))
            {
                Assert.Contains("-LiteralPath", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_script_removes_attack_surface_from_a_new_vm()
    {
        var text = HyperVScript.Text;
        var createVm = text[text.IndexOf("'CreateVm' {", StringComparison.Ordinal)..text.IndexOf("'Start' {", StringComparison.Ordinal)];

        foreach (var guid in new[] { "6C09BB55-D683-4DA0-8931-C9BF705F6480", "2A34B1C2-FD73-4043-8A5B-DD2159BC743F", "5CED1297-4598-4915-A5FC-AD21BB4D02A4" })
        {
            Assert.Contains(guid, createVm, StringComparison.Ordinal);
        }

        Assert.Contains("Disable-VMIntegrationService", createVm, StringComparison.Ordinal);
        Assert.Contains("Get-VMDvdDrive -VM $vm | Remove-VMDvdDrive", createVm, StringComparison.Ordinal);
        Assert.Contains("Get-VMNetworkAdapter -VM $vm | Disconnect-VMNetworkAdapter", createVm, StringComparison.Ordinal);
        Assert.Contains("-ExposeVirtualizationExtensions $false", createVm, StringComparison.Ordinal);

        // The needed services and host-wide settings are untouched.
        Assert.Equal(1, CountOf(createVm, "Disable-VMIntegrationService"));
        Assert.DoesNotContain("Set-VMHost", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-SwitchName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-VMNetworkAdapter", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_script_is_installed_atomically_and_no_temp_file_is_left()
    {
        await _host.StartAsync(_good, Ct);

        var files = Directory.GetFiles(Path.Combine(_root, "scripts"));
        var script = Assert.Single(files);
        Assert.EndsWith(".ps1", script, StringComparison.Ordinal);
        Assert.Equal(HyperVScript.Text, await File.ReadAllTextAsync(script, Ct));
    }

    [Fact]
    public async Task A_script_that_was_replaced_by_a_link_is_replaced_by_a_real_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a symbolic link needs a privilege on Windows.
        }

        await _host.StartAsync(_good, Ct);
        var script = Assert.Single(Directory.GetFiles(Path.Combine(_root, "scripts")));
        var evil = Path.Combine(_root, "evil.ps1");
        await File.WriteAllTextAsync(evil, HyperVScript.Text, Ct);
        File.Delete(script);
        File.CreateSymbolicLink(script, evil);

        await _host.StartAsync(_good, Ct);

        Assert.Null(new FileInfo(script).LinkTarget);
        Assert.Equal(HyperVScript.Text, await File.ReadAllTextAsync(script, Ct));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ParentDisk() => OperatingSystem.IsWindows() ? @"C:\images\golden.vhdx" : "/images/golden.vhdx";

    private sealed class RecordingRunner : IHyperVCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<HyperVCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(new HyperVCommandResult(0, "", ""));
        }
    }
}
