using Bazaar.Conduit.Ssh;
using Bazaar.Conduit.Tests.Cloudflared;
using Bazaar.Conduit.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.Ssh;

public sealed class SshReverseModuleTests
{
    private const string _host = "jump.example.test";
    private const string _user = "tunnel";
    private const int _remotePort = 9000;
    private const int _localPort = 7443;

    private static Dictionary<string, object> ValidSettings() => new()
    {
        ["conduit.ssh.jump_host"] = _host,
        ["conduit.ssh.jump_user"] = _user,
        ["conduit.ssh.remote_port"] = _remotePort,
    };

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Flag {flag} is missing or has no value.");
        return arguments[index + 1];
    }

    [Fact]
    public void Identity_matches_the_doc_catalogue()
    {
        var module = new SshReverseModule(new FakeSshProcessLauncher());

        Assert.Equal("ssh", module.Id);
        Assert.Equal(ConduitScope.Internet, module.Scope);
        Assert.Equal(75, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Start_builds_a_reverse_forward_with_strict_options_and_the_host_last()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), ValidSettings(), _localPort), CancellationToken.None);

        var call = Assert.Single(launcher.Calls);
        Assert.Equal("ssh", call.FileName);
        var arguments = call.Arguments;
        Assert.Contains("-N", arguments);
        Assert.Equal($"{_remotePort}:127.0.0.1:{_localPort}", ArgumentAfter(arguments, "-R"));
        Assert.Contains("ExitOnForwardFailure=yes", arguments);
        Assert.Contains("ServerAliveInterval=30", arguments);
        Assert.Contains("BatchMode=yes", arguments);
        Assert.Contains("StrictHostKeyChecking=yes", arguments);
        Assert.Equal("22", ArgumentAfter(arguments, "-p"));
        Assert.Equal($"{_user}@{_host}", arguments[^1]);
        Assert.DoesNotContain("-i", arguments);
    }

    [Fact]
    public async Task Identity_file_is_passed_as_a_path_argument()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var settings = ValidSettings();
        settings["conduit.ssh.identity_file"] = "/keys/conduit_ed25519";

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), settings, _localPort), CancellationToken.None);

        Assert.Equal("/keys/conduit_ed25519", ArgumentAfter(launcher.Calls[0].Arguments, "-i"));
    }

    [Theory]
    [InlineData("jump.example.test;id")]
    [InlineData("$(id)")]
    [InlineData("jump.example.test && curl evil")]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("user@jump.example.test")]
    [InlineData("jump host.example.test")]
    public async Task Host_with_shell_or_option_syntax_is_rejected_before_any_process_starts(string host)
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var settings = ValidSettings();
        settings["conduit.ssh.jump_host"] = host;

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), settings), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-host", module.Status.Reason);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Missing_jump_user_fails_not_configured()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var settings = ValidSettings();
        settings.Remove("conduit.ssh.jump_user");

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), settings), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("not-configured", module.Status.Reason);
        Assert.Equal(0, launcher.StartCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Remote_port_outside_1_to_65535_fails_invalid_remote_port(int port)
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var settings = ValidSettings();
        settings["conduit.ssh.remote_port"] = port;

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), settings), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-remote-port", module.Status.Reason);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Offer_appears_only_after_three_seconds_on_the_fake_clock()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, ValidSettings(), _localPort), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2.9));
        await CloudflaredTestSupport.Settle();

        Assert.Equal(ConduitModuleState.Starting, module.Status.State);
        Assert.Empty(module.Status.Offers);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await CloudflaredTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Running, "ssh offer");

        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal(new Uri($"wss://{_host}:{_remotePort}/ws"), offer.Uri);
        Assert.Equal("ssh", offer.ModuleId);
        Assert.Equal(ConduitScope.Internet, offer.Scope);
        Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
        Assert.Null(offer.ExpiresAt);
        Assert.False(offer.Verified);
        Assert.False(offer.RequiresRelay);
        Assert.False(offer.TerminatesTls);
    }

    [Fact]
    public async Task Exit_degrades_the_module_and_withdraws_the_offer()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, ValidSettings(), _localPort), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3));
        await CloudflaredTestSupport.Eventually(() => module.Status.Offers.Count == 1, "ssh offer");

        launcher.Process(0).Exit(255);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "degraded state");

        Assert.Equal("ssh-exited", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Restart_happens_after_five_seconds()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, ValidSettings(), _localPort), CancellationToken.None);
        launcher.Process(0).Exit(255);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "degraded state");

        time.Advance(TimeSpan.FromSeconds(4.9));
        await CloudflaredTestSupport.Settle();
        Assert.Equal(1, launcher.StartCount);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await CloudflaredTestSupport.Eventually(() => launcher.StartCount == 2, "restart");
        Assert.Equal(ConduitModuleState.Starting, module.Status.State);
    }

    [Fact]
    public async Task Repeated_early_exits_back_off_to_ten_seconds()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, ValidSettings(), _localPort), CancellationToken.None);
        launcher.Process(0).Exit(255);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "first degraded");
        time.Advance(TimeSpan.FromSeconds(5));
        await CloudflaredTestSupport.Eventually(() => launcher.StartCount == 2, "first restart");

        launcher.Process(1).Exit(255);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "second degraded");
        time.Advance(TimeSpan.FromSeconds(9.9));
        await CloudflaredTestSupport.Settle();
        Assert.Equal(2, launcher.StartCount);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await CloudflaredTestSupport.Eventually(() => launcher.StartCount == 3, "second restart");
    }

    [Fact]
    public async Task Stop_kills_the_process_and_withdraws_the_offer()
    {
        var launcher = new FakeSshProcessLauncher();
        var module = new SshReverseModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, ValidSettings(), _localPort), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3));
        await CloudflaredTestSupport.Eventually(() => module.Status.Offers.Count == 1, "ssh offer");

        await module.StopAsync(CancellationToken.None);

        Assert.True(launcher.Process(0).Killed);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Missing_binary_fails_binary_not_found()
    {
        var launcher = new FakeSshProcessLauncher { FailWith = new FileNotFoundException("ssh") };
        var module = new SshReverseModule(launcher);

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), ValidSettings(), _localPort), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("binary-not-found", module.Status.Reason);
    }

    [Fact]
    public async Task Real_launcher_with_a_missing_binary_fails_binary_not_found()
    {
        var module = new SshReverseModule(new SystemSshProcessLauncher());
        var settings = ValidSettings();
        settings["conduit.ssh.binary_path"] = "/nonexistent/bazaar-ssh-missing";

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), settings, _localPort), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("binary-not-found", module.Status.Reason);
    }
}
