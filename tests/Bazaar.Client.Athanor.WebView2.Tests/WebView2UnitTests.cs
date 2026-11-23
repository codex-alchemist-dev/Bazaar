using Bazaar.Client.Athanor.Bundle;
using Microsoft.Web.WebView2.Core;

namespace Bazaar.Client.Athanor.WebView2.Tests;

public sealed class WebView2UnitTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, EditorCrashReason.RendererExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, EditorCrashReason.RendererExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, EditorCrashReason.RendererExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, EditorCrashReason.RendererExited, false)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited, EditorCrashReason.Unknown, true)]
    public void Process_failures_map_to_a_crash_reason_and_say_whether_a_reload_recovers(
        CoreWebView2ProcessFailedKind kind, EditorCrashReason reason, bool canReload)
    {
        ProcessFailureOutcome outcome = WebView2EditorTransport.MapProcessFailure(kind);

        Assert.Equal(reason, outcome.Reason);
        Assert.Equal(canReload, outcome.CanReload);
    }

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited)]
    public void Auxiliary_process_restarts_are_not_reported_as_editor_crashes(CoreWebView2ProcessFailedKind kind)
    {
        Assert.Null(WebView2EditorTransport.MapProcessFailure(kind).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    public void Options_require_an_absolute_user_data_folder(string folder)
    {
        var options = new WebView2HostOptions { UserDataFolder = folder };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Options_accept_an_absolute_user_data_folder_and_default_to_the_built_bundle()
    {
        var options = new WebView2HostOptions { UserDataFolder = Path.Combine(Path.GetTempPath(), "wv2") };

        options.Validate();

        Assert.Equal(AthanorBundle.DefaultRoot, options.ResolvedBundleRoot);
        Assert.True(options.VerifyBundle);
        Assert.True(options.ReloadAfterCrash);
        Assert.False(options.EnableDevToolsProtocol);
    }

    [Fact]
    public void Origin_is_a_reserved_host_that_cannot_resolve()
    {
        Assert.Equal("https://athanor.invalid/", WebView2HostOptions.Origin.AbsoluteUri);
        Assert.EndsWith(".invalid", WebView2HostOptions.VirtualHost, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_refuses_to_post_before_it_is_attached()
    {
        var transport = new WebView2EditorTransport();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.PostAsync("{}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Transport_reports_a_fault_until_it_is_disposed()
    {
        var transport = new WebView2EditorTransport();
        var seen = new List<EditorCrashReason>();
        transport.Faulted += (_, r) => seen.Add(r);

        transport.ReportFault(EditorCrashReason.BridgeFault);
        await transport.DisposeAsync();
        transport.ReportFault(EditorCrashReason.Unknown);

        Assert.Equal([EditorCrashReason.BridgeFault], seen);
    }

    [Fact]
    public async Task Surface_without_a_complete_bundle_reports_the_failure_and_crashes_the_host()
    {
        string empty = Directory.CreateTempSubdirectory("athanor-nobundle-").FullName;
        try
        {
            var options = new WebView2HostOptions { UserDataFolder = Path.Combine(empty, "profile"), BundleRoot = empty };
            await using var surface = new WebView2EditorSurface(options);
            EditorCrashReason? reason = null;
            surface.Host.Crashed += (_, r) => reason = r;

            await surface.AttachAsync(1);

            Assert.Equal(EditorCrashReason.BridgeFault, reason);
            Assert.Contains("missing", surface.FailureMessage, StringComparison.Ordinal);
            Assert.True(surface.Host.IsCrashed);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task Surface_requires_a_parent_window()
    {
        var options = new WebView2HostOptions { UserDataFolder = Path.Combine(Path.GetTempPath(), "wv2-unused") };
        await using var surface = new WebView2EditorSurface(options);

        await Assert.ThrowsAsync<ArgumentException>(() => surface.AttachAsync(0));
    }
}
