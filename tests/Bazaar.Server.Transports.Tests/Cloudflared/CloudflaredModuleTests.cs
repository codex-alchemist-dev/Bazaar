using System.ComponentModel;
using Bazaar.Server.Transports.Cloudflared;
using Bazaar.Server.Transports.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Transports.Tests.Cloudflared;

public sealed class CloudflaredModuleTests
{
    private const string _token = "eyJ-test-tunnel-token-do-not-log";
    private const string _quickLine = "2026-11-09T10:00:00Z INF |  https://quick-fox-1234.trycloudflare.com  |";
    private const string _registeredLine = "2026-11-09T10:00:01Z INF Registered tunnel connection connIndex=0";
    private static readonly string[] _quickArguments = ["tunnel", "--url", "http://127.0.0.1:7443"];
    private static readonly string[] _namedArguments = ["tunnel", "run"];

    private static Dictionary<string, object> QuickSettings() => new() { ["transport.cloudflared.mode"] = "quick" };

    private static Dictionary<string, object> NamedSettings(string? hostname = "tunnel.example.test", string? token = _token)
    {
        var values = new Dictionary<string, object> { ["transport.cloudflared.mode"] = "named" };
        if (hostname is not null)
        {
            values["transport.cloudflared.hostname"] = hostname;
        }

        if (token is not null)
        {
            values["transport.cloudflared.tunnel_token"] = token;
        }

        return values;
    }

    [Fact]
    public void Identity_matches_the_doc_catalogue()
    {
        var module = new CloudflaredModule(new FakeCloudflaredLauncher());

        Assert.Equal("cloudflared", module.Id);
        Assert.Equal("Cloudflare Tunnel", module.DisplayName);
        Assert.Equal(TransportScope.Internet, module.Scope);
        Assert.Equal(30, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(TransportModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Quick_mode_publishes_the_parsed_wss_host_with_TerminatesTls()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, QuickSettings(), port: 7443), CancellationToken.None);

        Assert.Equal(TransportModuleState.Starting, module.Status.State);
        Assert.Empty(module.Status.Offers);
        Assert.Equal("cloudflared", launcher.Calls[0].FileName);
        Assert.Equal(_quickArguments, launcher.Calls[0].Arguments);
        Assert.Empty(launcher.Calls[0].Environment);

        launcher.Process(0).WriteStderr(_quickLine);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Running, "quick offer");

        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal(new Uri("wss://quick-fox-1234.trycloudflare.com/ws"), offer.Uri);
        Assert.Equal("cloudflared", offer.ModuleId);
        Assert.Equal(TransportScope.Internet, offer.Scope);
        Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
        Assert.Null(offer.ExpiresAt);
        Assert.False(offer.Verified);
        Assert.False(offer.RequiresRelay);
        Assert.True(offer.TerminatesTls);
    }

    [Fact]
    public async Task Named_mode_without_a_hostname_fails_not_configured()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: NamedSettings(hostname: null)), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("not-configured", module.Status.Reason);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task Named_mode_without_a_token_fails_not_configured()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: NamedSettings(token: null)), CancellationToken.None);

        Assert.Equal("not-configured", module.Status.Reason);
        Assert.Empty(launcher.Calls);
    }

    [Theory]
    [InlineData("https://tunnel.example.test")]
    [InlineData("203.0.113.5")]
    [InlineData("tunnel.example.test/ws")]
    public async Task Named_mode_rejects_a_hostname_that_is_not_a_bare_host(string hostname)
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: NamedSettings(hostname: hostname)), CancellationToken.None);

        Assert.Equal("invalid-hostname", module.Status.Reason);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task Unknown_mode_fails_invalid_mode()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        var settings = new Dictionary<string, object> { ["transport.cloudflared.mode"] = "tcp" };

        await module.StartAsync(Contexts.Create(settings: settings), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-mode", module.Status.Reason);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task The_tunnel_token_goes_in_the_environment_and_never_in_the_arguments()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: NamedSettings()), CancellationToken.None);

        var call = Assert.Single(launcher.Calls);
        Assert.Equal(_token, call.Environment["TUNNEL_TOKEN"]);
        Assert.Equal(_namedArguments, call.Arguments);
        Assert.DoesNotContain(call.Arguments, argument => argument.Contains(_token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Named_mode_publishes_the_configured_hostname_after_the_registered_line()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), NamedSettings()), CancellationToken.None);

        Assert.Equal(TransportModuleState.Starting, module.Status.State);
        Assert.Empty(module.Status.Offers);

        launcher.Process(0).WriteStderr(_registeredLine);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Running, "named offer");

        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal(new Uri("wss://tunnel.example.test/ws"), offer.Uri);
        Assert.True(offer.TerminatesTls);
        Assert.False(offer.Verified);
        Assert.Null(offer.ExpiresAt);
    }

    [Fact]
    public async Task Named_mode_publishes_after_five_seconds_without_an_exit()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        var time = new FakeTimeProvider();

        await module.StartAsync(Contexts.Create(time, NamedSettings()), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        await CloudflaredTestSupport.Settle();
        Assert.Equal(TransportModuleState.Starting, module.Status.State);

        time.Advance(TimeSpan.FromSeconds(1));
        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Running, "five second publish");
        Assert.Equal("tunnel.example.test", Assert.Single(module.Status.Offers).Uri.Host);
    }

    [Fact]
    public async Task Process_exit_degrades_the_module_and_withdraws_its_offers()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), QuickSettings()), CancellationToken.None);
        launcher.Process(0).WriteStderr(_quickLine);
        await CloudflaredTestSupport.Eventually(() => module.Status.Offers.Count == 1, "quick offer");

        launcher.Process(0).Exit(1);

        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Degraded, "degraded");
        Assert.Equal("process-exited", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Restart_waits_for_the_backoff_then_escalates_on_the_fake_clock()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        var time = new FakeTimeProvider();
        await module.StartAsync(Contexts.Create(time, QuickSettings()), CancellationToken.None);

        // First exit: 5 second backoff.
        launcher.Process(0).Exit(1);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Degraded, "first degrade");
        time.Advance(TimeSpan.FromSeconds(4));
        await CloudflaredTestSupport.Settle();
        Assert.Equal(1, launcher.StartCount);

        time.Advance(TimeSpan.FromSeconds(1));
        await CloudflaredTestSupport.Eventually(() => launcher.StartCount == 2, "first restart");

        // Second exit without ever publishing: 10 second backoff.
        launcher.Process(1).Exit(1);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Degraded, "second degrade");
        time.Advance(TimeSpan.FromSeconds(9));
        await CloudflaredTestSupport.Settle();
        Assert.Equal(2, launcher.StartCount);

        time.Advance(TimeSpan.FromSeconds(1));
        await CloudflaredTestSupport.Eventually(() => launcher.StartCount == 3, "second restart");
    }

    [Fact]
    public async Task Stop_during_backoff_prevents_the_restart()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        var time = new FakeTimeProvider();
        await module.StartAsync(Contexts.Create(time, QuickSettings()), CancellationToken.None);
        launcher.Process(0).Exit(1);
        await CloudflaredTestSupport.Eventually(() => module.Status.State == TransportModuleState.Degraded, "degraded");

        await module.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await CloudflaredTestSupport.Settle();

        Assert.Equal(1, launcher.StartCount);
        Assert.Equal(TransportModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Stop_kills_the_child_process_and_withdraws_offers()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        await module.StartAsync(Contexts.Create(new FakeTimeProvider(), QuickSettings()), CancellationToken.None);
        launcher.Process(0).WriteStderr(_quickLine);
        await CloudflaredTestSupport.Eventually(() => module.Status.Offers.Count == 1, "quick offer");

        await module.StopAsync(CancellationToken.None);

        Assert.True(launcher.Process(0).Killed);
        Assert.Equal(TransportModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task A_missing_binary_fails_binary_not_found_without_throwing()
    {
        var launcher = new FakeCloudflaredLauncher { FailWith = new FileNotFoundException("cloudflared") };
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: QuickSettings()), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("binary-not-found", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        await module.StopAsync(CancellationToken.None);
        Assert.Equal(TransportModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task A_Win32_file_not_found_error_also_means_binary_not_found()
    {
        var launcher = new FakeCloudflaredLauncher { FailWith = new Win32Exception(2) };
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: QuickSettings()), CancellationToken.None);

        Assert.Equal("binary-not-found", module.Status.Reason);
    }

    [Fact]
    public async Task Other_launch_errors_report_the_exception_type_name()
    {
        var launcher = new FakeCloudflaredLauncher { FailWith = new InvalidOperationException("secret detail") };
        var module = new CloudflaredModule(launcher);

        await module.StartAsync(Contexts.Create(settings: QuickSettings()), CancellationToken.None);

        Assert.Equal(TransportModuleState.Failed, module.Status.State);
        Assert.Equal("InvalidOperationException", module.Status.Reason);
    }

    [Fact]
    public async Task Binary_path_setting_is_used_as_the_file_name()
    {
        var launcher = new FakeCloudflaredLauncher();
        var module = new CloudflaredModule(launcher);
        var settings = QuickSettings();
        settings["transport.cloudflared.binary_path"] = "/opt/cloudflared/bin/cloudflared";

        await module.StartAsync(Contexts.Create(settings: settings), CancellationToken.None);

        Assert.Equal("/opt/cloudflared/bin/cloudflared", launcher.Calls[0].FileName);
    }
}
