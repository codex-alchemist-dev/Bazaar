using System.Xml.Linq;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.WindowsSandbox.WsbCli;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests.Adversarial;

/// <summary>Attacks on the .wsb config, the wsb command lines and the files the host adapter reads and deletes. Uses the fake runner; no sandbox starts.</summary>
public sealed class WindowsSandboxAdversarialTests : IDisposable
{
    private const string _good = "<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><MemoryInMB>4096</MemoryInMB>"
        + "<ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput>"
        + "<PrinterRedirection>Disable</PrinterRedirection><ProtectedClient>Enable</ProtectedClient>@@EXTRA@@</Configuration>";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-wsb-adv-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWsbCommandRunner _runner = new();
    private readonly WsbCliSandboxHost _host;

    public WindowsSandboxAdversarialTests()
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

    private static string Config(string extra = "") => _good.Replace("@@EXTRA@@", extra, StringComparison.Ordinal);

    private static VmSpec Spec() => new(VmOs.Windows, 2, 4096, 60);

    [Fact]
    public void Built_config_states_every_restrictive_switch_explicitly()
    {
        var root = XElement.Parse(WindowsSandboxConfig.Build(Spec(), WindowsSandboxNetworking.Disable));

        Assert.Equal("Disable", root.Element("Networking")!.Value);
        Assert.Equal("Disable", root.Element("VGpu")!.Value);
        Assert.Equal("Disable", root.Element("ClipboardRedirection")!.Value);
        Assert.Equal("Disable", root.Element("PrinterRedirection")!.Value);
        Assert.Equal("Disable", root.Element("AudioInput")!.Value);
        Assert.Equal("Disable", root.Element("VideoInput")!.Value);
        Assert.Equal("Enable", root.Element("ProtectedClient")!.Value);
        Assert.Null(root.Element("LogonCommand"));
    }

    [Fact]
    public void An_undefined_networking_mode_is_refused_instead_of_written_as_a_number()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsSandboxConfig.Build(Spec(), (WindowsSandboxNetworking)7));
    }

    [Theory]
    [InlineData(@"\\server\share\agent")]
    [InlineData(@"\\?\C:\agent")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"relative\agent")]
    [InlineData(@"C:agent")]
    [InlineData(@"C:\agent\..\..\Windows")]
    [InlineData(@"C:\agent\..")]
    [InlineData(@"C:\agent:stream")]
    [InlineData("C:\\agent\nfolder")]
    [InlineData("C:\\agent\0")]
    [InlineData(@"C:\agent.")]
    [InlineData(@"/etc")]
    public void An_unsafe_agent_folder_is_refused(string folder)
    {
        Assert.Throws<ArgumentException>(() => WindowsSandboxConfig.Build(Spec(), WindowsSandboxNetworking.Disable, folder));
    }

    [Fact]
    public void XML_metacharacters_in_the_agent_folder_stay_text_and_cannot_add_elements()
    {
        var xml = WindowsSandboxConfig.Build(Spec(), WindowsSandboxNetworking.Disable, @"C:\a&b\c'd");

        var root = XElement.Parse(xml);
        Assert.Equal(@"C:\a&b\c'd", root.Element("MappedFolders")!.Element("MappedFolder")!.Element("HostFolder")!.Value);
        Assert.Equal("true", root.Element("MappedFolders")!.Element("MappedFolder")!.Element("ReadOnly")!.Value);

        // '<', '>' and '"' are not legal in a Windows path; they are refused rather than escaped.
        Assert.Throws<ArgumentException>(() => WindowsSandboxConfig.Build(Spec(), WindowsSandboxNetworking.Disable, @"C:\a</HostFolder><LogonCommand>"));
        Assert.Throws<ArgumentException>(() => WindowsSandboxConfig.Build(Spec(), WindowsSandboxNetworking.Disable, "C:\\a\"b"));
    }

    [Theory]
    [InlineData("<ClipboardRedirection>Enable</ClipboardRedirection>", "clipboard on")]
    [InlineData("<Networking>Bogus</Networking>", "unknown networking")]
    [InlineData("<LogonCommand><Command>cmd /c calc</Command></LogonCommand>", "logon command")]
    [InlineData("<Surprise>1</Surprise>", "unknown element")]
    [InlineData("<Networking>Default</Networking>", "duplicate element")]
    [InlineData("<MappedFolders><MappedFolder><HostFolder>C:\\x</HostFolder><SandboxFolder>C:\\y</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders>", "writable folder")]
    [InlineData("<MappedFolders><MappedFolder><HostFolder>C:\\x</HostFolder><SandboxFolder>C:\\y</SandboxFolder></MappedFolder></MappedFolders>", "read-only left implicit")]
    [InlineData("<MappedFolders><MappedFolder><HostFolder>\\\\evil\\share</HostFolder><SandboxFolder>C:\\y</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder></MappedFolders>", "unc folder")]
    [InlineData("<MappedFolders><MappedFolder><HostFolder>..\\x</HostFolder><SandboxFolder>C:\\y</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder></MappedFolders>", "relative folder")]
    public async Task A_config_that_loosens_the_floor_is_neither_written_nor_launched(string extra, string why)
    {
        var xml = Config(extra);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.WriteConfigAsync("agent-x", xml, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_directory, "agent-x.wsb")), why);
    }

    [Theory]
    [InlineData("VGpu", "Enable")]
    [InlineData("ClipboardRedirection", "Default")]
    [InlineData("PrinterRedirection", "Enable")]
    [InlineData("AudioInput", "Enable")]
    [InlineData("VideoInput", "Enable")]
    [InlineData("ProtectedClient", "Disable")]
    public async Task Every_loosened_switch_is_refused(string element, string value)
    {
        var xml = Config().Replace($"<{element}>{(element == "ProtectedClient" ? "Enable" : "Disable")}</{element}>", $"<{element}>{value}</{element}>", StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.WriteConfigAsync("agent-x", xml, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("VGpu")]
    [InlineData("ClipboardRedirection")]
    [InlineData("AudioInput")]
    [InlineData("VideoInput")]
    [InlineData("PrinterRedirection")]
    [InlineData("ProtectedClient")]
    [InlineData("Networking")]
    public async Task A_switch_left_out_is_refused_because_wsb_would_default_it_to_the_permissive_value(string element)
    {
        var xml = XElement.Parse(Config());
        xml.Element(element)!.Remove();

        await Assert.ThrowsAsync<ArgumentException>(() => _host.WriteConfigAsync("agent-x", xml.ToString(SaveOptions.DisableFormatting), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_config_edited_on_disk_after_it_was_written_is_refused_at_launch()
    {
        var path = await _host.WriteConfigAsync("agent-x", Config(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, Config("<LogonCommand><Command>x</Command></LogonCommand>"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.LaunchAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task An_entity_declaration_in_the_config_is_not_expanded()
    {
        var xml = "<!DOCTYPE c [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>" + Config();

        await Assert.ThrowsAsync<ArgumentException>(() => _host.WriteConfigAsync("agent-x", xml, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Launch_and_delete_refuse_paths_outside_the_config_directory()
    {
        var outside = Path.Combine(Path.GetTempPath(), "bazaar-wsb-outside-" + Guid.NewGuid().ToString("N") + ".wsb");
        await File.WriteAllTextAsync(outside, Config(), TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _host.LaunchAsync(outside, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => _host.DeleteConfigAsync(outside, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => _host.DeleteConfigAsync(Path.Combine(_directory, "..", Path.GetFileName(outside)), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(outside), "the file outside the directory must survive");
            Assert.Empty(_runner.Calls);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Delete_refuses_a_file_that_is_not_a_wsb_config()
    {
        Directory.CreateDirectory(_directory);
        var other = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(other, "keep", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.DeleteConfigAsync(other, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData("--raw")]
    [InlineData("-c")]
    [InlineData("abc def")]
    [InlineData("abc\"; calc")]
    [InlineData("abc\n")]
    [InlineData("")]
    public async Task An_instance_id_that_could_inject_an_option_is_refused_before_any_command_runs(string id)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ShutdownAsync(id, force: true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ExecAsync(id, ["tool.exe"], TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.IsRunningAsync(id, TestContext.Current.CancellationToken));
        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("{\"Id\":\"--id\"}")]
    [InlineData("{\"Id\":\"abc; calc\"}")]
    [InlineData("{\"Id\":\"-rf\"}")]
    [InlineData("{\"Id\":\"abc\\ndef\"}")]
    public async Task A_sandbox_id_printed_by_wsb_start_that_is_not_a_plain_token_is_refused(string output)
    {
        var path = await _host.WriteConfigAsync("agent-x", Config(), TestContext.Current.CancellationToken);
        _runner.Enqueue(new WsbCommandResult(0, output, ""));

        await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.LaunchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_sandbox_id_in_the_list_output_that_is_not_a_plain_token_is_refused()
    {
        _runner.Enqueue(new WsbCommandResult(0, "{\"WindowsSandboxEnvironments\":[{\"Id\":\"--raw\"}]}", ""));

        await Assert.ThrowsAsync<WindowsSandboxCliException>(() => _host.CountRunningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_guid_id_is_accepted()
    {
        var id = Guid.NewGuid().ToString("D");
        _runner.Enqueue(new WsbCommandResult(0, "{}", ""));

        await _host.ShutdownAsync(id, force: false, TestContext.Current.CancellationToken);

        Assert.Equal<string>(["stop", "--id", id, "--raw"], Assert.Single(_runner.Calls));
    }

    [Fact]
    public async Task Exec_keeps_a_hostile_argument_as_one_quoted_part_of_the_command_line()
    {
        _runner.Enqueue(new WsbCommandResult(0, "", ""));

        await _host.ExecAsync("abc-123", [@"C:\agent\agent.exe", "x\" & calc & \"y", "\\\"", "a b\\"], TestContext.Current.CancellationToken);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("exec", call[0]);
        Assert.Equal(8, call.Count);
        var commandLine = call[6];
        Assert.StartsWith(@"C:\agent\agent.exe ""x\"" & calc & \""y""", commandLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-evil.exe")]
    [InlineData("   ")]
    public async Task Exec_refuses_a_program_path_that_looks_like_an_option_or_is_blank(string program)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.ExecAsync("abc-123", [program], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exec_refuses_a_nul_in_any_part()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.ExecAsync("abc-123", ["tool.exe", "a\0b"], TestContext.Current.CancellationToken));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task The_launch_command_line_never_includes_a_logon_command_or_shell()
    {
        var path = await _host.WriteConfigAsync("agent-x", WindowsSandboxConfig.Build(Spec(), WindowsSandboxNetworking.Disable, @"C:\agent"), TestContext.Current.CancellationToken);
        _runner.Enqueue(new WsbCommandResult(0, "{\"Id\":\"" + Guid.NewGuid() + "\"}", ""));

        await _host.LaunchAsync(path, TestContext.Current.CancellationToken);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["start", "-c"], call.Take(2));
        Assert.DoesNotContain("LogonCommand", call[2], StringComparison.Ordinal);
        Assert.Equal("--raw", call[3]);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData("con.")]
    public async Task Instance_names_that_could_climb_out_of_the_config_directory_are_refused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.WriteConfigAsync(name, Config(), TestContext.Current.CancellationToken));
    }
}
