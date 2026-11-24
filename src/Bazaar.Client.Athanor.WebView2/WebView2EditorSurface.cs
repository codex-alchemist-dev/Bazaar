using System.Text.Json;
using Bazaar.Client.Athanor.Bundle;
using Bazaar.Client.Athanor.Hosting;
using Microsoft.Web.WebView2.Core;

namespace Bazaar.Client.Athanor.WebView2;

/// <summary>
/// One WebView2 editor surface parented to a window handle: the controller, the locked-down core settings, the bundle
/// mapping and the bridge host. It knows nothing about Avalonia, so it can be driven by any window (and by the
/// integration tests with a plain Win32 window). <see cref="WebView2EditorHost"/> is the Avalonia face of it.
/// </summary>
/// <remarks>
/// The bridge host exists from construction, so callers can queue documents and a theme at once; the transport is
/// attached when <see cref="AttachAsync"/> finishes. All members run on the thread that owns the window.
/// </remarks>
public sealed class WebView2EditorSurface : IAsyncDisposable
{
    private readonly WebView2HostOptions _options;
    private readonly WebView2EditorTransport _transport = new();
    private CoreWebView2Controller? _controller;
    private bool _disposed;

    public WebView2EditorSurface(
        WebView2HostOptions options,
        IEditorKeyHandoff? handoff = null,
        IClaimedKeySource? claims = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        Host = new BridgeEditorHost(_transport, handoff, claims);
    }

    /// <summary>The bridge host. Usable at once; messages wait until the page is ready.</summary>
    public BridgeEditorHost Host { get; }

    /// <summary>The core once attached; null before. Exposed for diagnostics and the integration tests.</summary>
    public CoreWebView2? Core => _controller?.CoreWebView2;

    /// <summary>Why the surface could not start (missing runtime, bad bundle), or null.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>The number of requests the surface refused because they left the bundle origin. Zero in a healthy run.</summary>
    public int BlockedRequestCount { get; private set; }

    /// <summary>Creates the controller under <paramref name="parentWindow"/> and loads the editor page.</summary>
    public async Task AttachAsync(nint parentWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (parentWindow == 0)
        {
            throw new ArgumentException("A parent window handle is required.", nameof(parentWindow));
        }

        string bundleRoot = _options.ResolvedBundleRoot;
        if (_options.VerifyBundle)
        {
            BundleVerification check = AthanorBundle.Verify(bundleRoot);
            if (!check.Complete)
            {
                Fail(check.Problem!, EditorCrashReason.BridgeFault);
                return;
            }
        }
        else if (!File.Exists(Path.Combine(bundleRoot, AthanorBundle.HostPage)))
        {
            Fail($"The bundle has no {AthanorBundle.HostPage}.", EditorCrashReason.BridgeFault);
            return;
        }

        CoreWebView2Environment environment;
        try
        {
            environment = await AthanorWebView2Environment.GetAsync(_options).ConfigureAwait(true);
            _controller = await environment.CreateCoreWebView2ControllerAsync(parentWindow).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Fail("WebView2 could not start: " + ex.Message, EditorCrashReason.Unknown);
            return;
        }

        if (_disposed)
        {
            _controller.Close();
            return;
        }

        CoreWebView2 core = _controller.CoreWebView2;
        LockDown(core, environment);
        _transport.Attach(core);
        core.ProcessFailed += OnProcessFailed;
        core.SetVirtualHostNameToFolderMapping(WebView2HostOptions.VirtualHost, bundleRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(new Uri(WebView2HostOptions.Origin, AthanorBundle.HostPage).AbsoluteUri);
    }

    /// <summary>Sets the controller bounds in pixels, relative to the parent window's client area.</summary>
    public void SetBounds(int width, int height)
    {
        if (_controller is not null && width > 0 && height > 0)
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        }
    }

    /// <summary>Moves native keyboard focus into the WebView2 window (the page decides what has focus inside it).</summary>
    public void MoveFocusIntoEditor() => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    /// <summary>Sends a DevTools protocol command. Needs <see cref="WebView2HostOptions.EnableDevToolsProtocol"/>; for tests and diagnostics only.</summary>
    public Task<string> CallDevToolsAsync(string method, object parameters) =>
        (Core ?? throw new InvalidOperationException("The surface is not attached.")).CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Host.DisposeAsync().ConfigureAwait(true);
        if (_controller is not null)
        {
            _controller.CoreWebView2.ProcessFailed -= OnProcessFailed;
            _controller.Close();
            _controller = null;
        }
    }

    private void Fail(string message, EditorCrashReason reason)
    {
        FailureMessage = message;
        _transport.ReportFault(reason);
    }

    private void LockDown(CoreWebView2 core, CoreWebView2Environment environment)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = _options.EnableDevToolsProtocol;
        settings.AreBrowserAcceleratorKeysEnabled = false; // no F5, F12 or print from the browser layer; Monaco keeps its own keys
        settings.AreHostObjectsAllowed = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsWebMessageEnabled = true;

        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(WebView2HostOptions.Origin.AbsoluteUri, StringComparison.Ordinal))
            {
                e.Cancel = true;
                BlockedRequestCount++;
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            BlockedRequestCount++;
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;

        // The host never touches the network (doc 28): anything not served from the bundle is answered locally with 403.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            string uri = e.Request.Uri;
            if (uri.StartsWith(WebView2HostOptions.Origin.AbsoluteUri, StringComparison.Ordinal)
                || uri.StartsWith("data:", StringComparison.Ordinal)
                || uri.StartsWith("blob:", StringComparison.Ordinal))
            {
                return;
            }

            BlockedRequestCount++;
            e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
        };
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ProcessFailureOutcome outcome = WebView2EditorTransport.MapProcessFailure(e.ProcessFailedKind);
        if (outcome.Reason is null)
        {
            return;
        }

        _transport.ReportFault(outcome.Reason.Value);
        if (outcome.CanReload && _options.ReloadAfterCrash && _controller is not null)
        {
            _controller.CoreWebView2.Reload();
        }
    }
}
