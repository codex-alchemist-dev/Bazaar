using System.Net;
using System.Text.Json;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Smb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class SmbProviderTests : IDisposable
{
    private readonly MountEnv _env = new(b => b.DenyPaths = []);
    private readonly FakeRunner _runner = new();
    private readonly List<string> _log = [];
    private readonly FakeIdentity _identity;
    private readonly FakeEndpoint _endpoint;
    private readonly SmbMountProvider _provider;

    public SmbProviderTests()
    {
        _identity = new FakeIdentity(_log);
        _endpoint = new FakeEndpoint(_log);
        _provider = new SmbMountProvider(_env.Options, _runner, _identity, _endpoint, NullLogger<SmbMountProvider>.Instance, requireDrivePath: false);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _env.Dispose();
    }

    private static JsonDocument Params(HostCommand command) => JsonDocument.Parse(command.StandardInput!);

    [Fact]
    public async Task A_soul_attach_creates_one_read_only_share_for_the_vm_account()
    {
        var request = _env.Request(MountKind.Soul, MountMode.Ro);

        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        var command = Assert.Single(_runner.Commands);
        Assert.Equal("powershell.exe", command.Tool);
        Assert.Equal(SmbScripts.Attach, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[3])));
        using var json = Params(command);
        Assert.Equal(SmbShareNames.For(request.AgentId, MountKind.Soul, request.MountId), json.RootElement.GetProperty("Name").GetString());
        Assert.Equal(_env.SoulPath, json.RootElement.GetProperty("Path").GetString());
        Assert.Equal(_identity.Account, json.RootElement.GetProperty("Account").GetString());
        Assert.Equal("Read", json.RootElement.GetProperty("Right").GetString());
        Assert.Contains("172.20.0.1", json.RootElement.GetProperty("Description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_default_a_path_without_a_drive_letter_never_reaches_a_script()
    {
        using var strict = new SmbMountProvider(_env.Options, _runner, _identity, _endpoint, NullLogger<SmbMountProvider>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => strict.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task A_workspace_attach_is_read_write_and_asks_for_its_own_account_kind()
    {
        var request = _env.Request(MountKind.Workspace, MountMode.Rw);

        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        using var json = Params(Assert.Single(_runner.Commands));
        Assert.Equal("Change", json.RootElement.GetProperty("Right").GetString());
        Assert.Contains("-work-", json.RootElement.GetProperty("Name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_workspace_request_is_honoured_not_widened()
    {
        await _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Ro), TestContext.Current.CancellationToken);

        using var json = Params(Assert.Single(_runner.Commands));
        Assert.Equal("Read", json.RootElement.GetProperty("Right").GetString());
    }

    [Fact]
    public async Task A_read_write_soul_request_is_refused_before_anything_runs()
    {
        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Soul, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Equal(MountRefusalReason.ModeNotAllowed, ex.Reason);
        Assert.Empty(_runner.Commands);
        Assert.Equal(0, _identity.Calls);
    }

    [Fact]
    public async Task The_endpoint_and_identity_are_consulted_before_the_script_runs()
    {
        _runner.Answer = c =>
        {
            _log.Add("script");
            return new HostCommandResult(0, "ok", string.Empty);
        };

        await _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken);

        Assert.Equal(["endpoint", "identity", "script"], _log);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("..")]
    public async Task Paths_outside_the_agents_root_are_refused(string relative)
    {
        var outside = Path.GetFullPath(Path.Combine(_env.AgentRoot, relative));
        var request = _env.Request(MountKind.Workspace, MountMode.Rw, outside);

        await Assert.ThrowsAsync<MountRefusedException>(() => _provider.AttachAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task Another_agents_folder_is_refused()
    {
        var other = Path.Combine(_env.Root, Guid.NewGuid().ToString("N"), "workspace");
        Directory.CreateDirectory(other);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw, other), TestContext.Current.CancellationToken));

        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task The_audit_folder_inside_the_workspace_is_refused()
    {
        var audit = Path.Combine(_env.WorkspacePath, "audit");
        Directory.CreateDirectory(audit);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw, audit), TestContext.Current.CancellationToken));

        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task A_link_on_the_path_is_refused()
    {
        var target = Path.Combine(_env.Root, "elsewhere");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_env.WorkspacePath, "sneaky");
        Directory.CreateSymbolicLink(link, target);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw, link), TestContext.Current.CancellationToken));

        Assert.Equal(MountRefusalReason.LinkNotAllowed, ex.Reason);
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task A_non_canonical_spelling_of_the_root_is_refused()
    {
        var spelled = _env.WorkspacePath + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;

        await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw, spelled), TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task Empty_ids_are_refused()
    {
        var request = _env.Request(MountKind.Workspace, MountMode.Rw) with { MountId = Guid.Empty };

        await Assert.ThrowsAsync<MountRefusedException>(() => _provider.AttachAsync(request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<MountRefusedException>(() => _provider.DetachAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Commands);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public async Task A_switch_address_that_is_not_private_is_refused(string address)
    {
        _endpoint.Address = IPAddress.Parse(address);

        await Assert.ThrowsAsync<SmbShareException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Commands);
        Assert.Equal(0, _identity.Calls);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.0.1")]
    [InlineData("192.168.5.1")]
    [InlineData("169.254.1.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public async Task Private_and_link_local_switch_addresses_are_accepted(string address)
    {
        _endpoint.Address = IPAddress.Parse(address);

        await _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken);

        Assert.Single(_runner.Commands);
    }

    [Fact]
    public async Task A_bad_account_name_from_the_identity_port_runs_nothing()
    {
        _identity.Account = "Everyone";

        await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task A_failing_script_is_an_error_with_a_cleaned_first_line()
    {
        _runner.Answer = _ => new HostCommandResult(1, string.Empty, "share access is not exactly the requested grant\u001b[31m\nstack trace line");

        var ex = await Assert.ThrowsAsync<SmbShareException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Contains("share access is not exactly", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', ex.Message);
        Assert.DoesNotContain("stack trace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_that_does_not_report_success_is_an_error()
    {
        _runner.Answer = _ => new HostCommandResult(0, "something else", string.Empty);

        await Assert.ThrowsAsync<SmbShareException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Attach_twice_runs_the_same_rebuild_script_each_time()
    {
        var request = _env.Request(MountKind.Workspace, MountMode.Rw);

        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);
        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, _runner.Commands.Count);
        Assert.Equal(_runner.Commands[0].StandardInput, _runner.Commands[1].StandardInput);
    }

    [Fact]
    public async Task Detach_removes_the_share_by_name_and_needs_neither_path_nor_account()
    {
        var request = _env.Request(MountKind.Soul, MountMode.Ro);
        Directory.Delete(_env.SoulPath);

        await _provider.DetachAsync(request, TestContext.Current.CancellationToken);

        var command = Assert.Single(_runner.Commands);
        Assert.Equal(SmbScripts.Remove, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[3])));
        using var json = Params(command);
        Assert.Equal(SmbShareNames.For(request.AgentId, MountKind.Soul, request.MountId), json.RootElement.GetProperty("Name").GetString());
        Assert.Equal(0, _identity.Calls);
        Assert.Equal(0, _endpoint.Calls);
    }

    [Fact]
    public async Task Detach_is_idempotent()
    {
        var request = _env.Request(MountKind.Workspace, MountMode.Rw);

        await _provider.DetachAsync(request, TestContext.Current.CancellationToken);
        await _provider.DetachAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, _runner.Commands.Count);
        Assert.Equal(_runner.Commands[0].StandardInput, _runner.Commands[1].StandardInput);
    }

    [Fact]
    public async Task A_failed_detach_surfaces_so_the_manager_keeps_the_row()
    {
        _runner.Answer = _ => new HostCommandResult(1, string.Empty, "access denied");

        await Assert.ThrowsAsync<SmbShareException>(
            () => _provider.DetachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Script_runs_never_overlap()
    {
        _runner.Delay = TimeSpan.FromMilliseconds(30);

        await Task.WhenAll(
            _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken),
            _provider.DetachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken),
            _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Equal(1, _runner.MaxConcurrent);
    }

    [Fact]
    public async Task The_canary_watch_is_a_disposable_no_op()
    {
        await using var watch = await _provider.WatchCanariesAsync(
            new CanaryWatch(Guid.NewGuid(), [".canary/credentials.json"]), _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.NotNull(watch);
    }

    [Fact]
    public async Task The_real_runner_refuses_off_windows_and_unknown_tools()
    {
        var runner = new ProcessHostCommandRunner();
        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                async () => await runner.RunAsync(new HostCommand("powershell.exe", []), TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(
                async () => await runner.RunAsync(new HostCommand("cmd.exe", ["/c", "calc"]), TestContext.Current.CancellationToken));
        }
    }
}
