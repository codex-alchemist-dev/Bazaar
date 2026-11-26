using System.Runtime.InteropServices;

namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>
/// The real <see cref="IEditorSurface"/>: one GTK top-level window with a WebKitGTK view that shows the Monaco bundle
/// (doc 57 section 2.1). The bundle is served from disk through the <c>athanor://app/</c> scheme, so there is no
/// network. Navigation to any other address and every new-window request are ignored. This type is not unit tested
/// (it needs libwebkit2gtk and a display); the opt-in test in Bazaar.Client.Athanor.Gtk.Tests drives it.
/// </summary>
internal sealed class GtkEditorSurface : IEditorSurface
{
    private const string _scheme = "athanor";
    private const string _origin = "athanor://app/";

    // Navigation policy decision types in WebKitGTK: 0 navigation action, 1 new window action, 2 response.
    private const int _decisionNavigation = 0;
    private const int _decisionNewWindow = 1;

    private readonly Queue<Action> _queue = new();
    private readonly object _queueLock = new();
    private readonly string _bundleRoot;
    private readonly string _entry;
    private readonly nint _freeAddress;
    private readonly GtkNative.IdleCallback _idle;
    private readonly nint _idlePointer;
    private readonly GtkNative.ScriptMessageCallback _onScript;
    private readonly GtkNative.SchemeRequestCallback _onScheme;
    private readonly GtkNative.DeleteEventCallback _onDelete;
    private readonly GtkNative.DecidePolicyCallback _onPolicy;
    private readonly GtkNative.WebProcessTerminatedCallback _onTerminated;
    private readonly GtkNative.LoadFailedCallback _onLoadFailed;
    private HelperCore? _core;
    private nint _window;
    private nint _webView;

    public GtkEditorSurface(string bundleRoot, string entry)
    {
        _bundleRoot = Path.GetFullPath(bundleRoot);
        _entry = entry;
        _freeAddress = GtkNative.GFreeAddress();
        _idle = RunQueued;
        _idlePointer = Marshal.GetFunctionPointerForDelegate(_idle);
        _onScript = OnScriptMessage;
        _onScheme = OnSchemeRequest;
        _onDelete = OnDeleteEvent;
        _onPolicy = OnDecidePolicy;
        _onTerminated = OnWebProcessTerminated;
        _onLoadFailed = OnLoadFailed;
    }

    /// <summary>The WebKitGTK version text, for <c>helper.hello</c>.</summary>
    public static string WebKitVersion() =>
        $"{GtkNative.GetMajorVersion()}.{GtkNative.GetMinorVersion()}.{GtkNative.GetMicroVersion()}";

    /// <summary>Builds the window and starts loading the bundle. Call on the thread that will run <see cref="RunMainLoop"/>.</summary>
    public void Create(HelperCore core, HelperOptions options)
    {
        _core = core;
        nint context = GtkNative.WebContextGetDefault();
        nint security = GtkNative.WebContextGetSecurityManager(context);
        GtkNative.SecurityManagerRegisterSchemeAsSecure(security, _scheme);
        GtkNative.SecurityManagerRegisterSchemeAsCorsEnabled(security, _scheme);
        GtkNative.WebContextRegisterUriScheme(context, _scheme, Marshal.GetFunctionPointerForDelegate(_onScheme), 0, 0);

        nint manager = GtkNative.UserContentManagerNew();
        GtkNative.UserContentManagerRegisterScriptMessageHandler(manager, "bridge", 0);
        GtkNative.SignalConnectData(manager, "script-message-received::bridge", Marshal.GetFunctionPointerForDelegate(_onScript), 0, 0, 0);

        _webView = GtkNative.WebViewNewWithUserContentManager(manager);
        GtkNative.SignalConnectData(_webView, "decide-policy", Marshal.GetFunctionPointerForDelegate(_onPolicy), 0, 0, 0);
        GtkNative.SignalConnectData(_webView, "web-process-terminated", Marshal.GetFunctionPointerForDelegate(_onTerminated), 0, 0, 0);
        GtkNative.SignalConnectData(_webView, "load-failed", Marshal.GetFunctionPointerForDelegate(_onLoadFailed), 0, 0, 0);

        _window = GtkNative.WindowNew(0);
        GtkNative.WindowSetTitle(_window, options.Title);
        GtkNative.WindowSetDefaultSize(_window, options.Width, options.Height);
        GtkNative.SignalConnectData(_window, "delete-event", Marshal.GetFunctionPointerForDelegate(_onDelete), 0, 0, 0);
        GtkNative.ContainerAdd(_window, _webView);
        GtkNative.WidgetShowAll(_window);
        GtkNative.WindowPresent(_window);
        GtkNative.WebViewLoadUri(_webView, _origin + _entry.Replace('\\', '/'));
    }

    public static void RunMainLoop() => GtkNative.RunMain();

    public void Dispatch(Action action)
    {
        lock (_queueLock)
        {
            _queue.Enqueue(action);
        }

        _ = GtkNative.IdleAdd(_idlePointer, 0);
    }

    public void PostToPage(string json)
    {
        // The JSON text is already validated as one JSON object; it is a script literal, never concatenated user text.
        GtkNative.WebViewEvaluateJavascript(_webView, "window.__fromHost(" + json + ")", -1, 0, 0, 0, 0, 0);
    }

    public void ApplyWindow(WindowPlacement placement)
    {
        if (placement.Title is not null)
        {
            GtkNative.WindowSetTitle(_window, placement.Title);
        }

        GtkNative.WindowResize(_window, placement.Width, placement.Height);
        GtkNative.WindowMove(_window, placement.X, placement.Y);
        if (placement.Visible)
        {
            GtkNative.WidgetShowAll(_window);
            GtkNative.WindowPresent(_window);
        }
        else
        {
            GtkNative.WidgetHide(_window);
        }
    }

    public void Quit() => GtkNative.MainQuit();

    private bool RunQueued(nint data)
    {
        Action? next;
        lock (_queueLock)
        {
            next = _queue.Count > 0 ? _queue.Dequeue() : null;
        }

        next?.Invoke();
        return false;
    }

    private void OnScriptMessage(nint manager, nint jscValue, nint data)
    {
        nint text = GtkNative.JscValueToString(GtkNative.JavascriptResultGetJsValue(jscValue));
        string? json = Marshal.PtrToStringUTF8(text);
        GtkNative.Free(text);
        if (json is not null)
        {
            _core?.OnPageMessage(json);
        }
    }

    private void OnSchemeRequest(nint request, nint data)
    {
        string path = Marshal.PtrToStringUTF8(GtkNative.UriSchemeRequestGetPath(request)) ?? "/";
        byte[] bytes = [];
        string mime = "text/plain";
        string root = _bundleRoot.EndsWith(Path.DirectorySeparatorChar) ? _bundleRoot : _bundleRoot + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(_bundleRoot, Uri.UnescapeDataString(path).TrimStart('/')));
        if (file.StartsWith(root, StringComparison.Ordinal) && File.Exists(file))
        {
            bytes = File.ReadAllBytes(file);
            mime = MimeFor(file);
        }

        // A missing file or a path outside the bundle gets an empty reply, never data from elsewhere.
        nint memory = GtkNative.Malloc((nuint)Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        nint stream = GtkNative.MemoryInputStreamNewFromData(memory, bytes.Length, _freeAddress);
        GtkNative.UriSchemeRequestFinish(request, stream, bytes.Length, mime);
    }

    private bool OnDeleteEvent(nint widget, nint gdkEvent, nint data)
    {
        GtkNative.WidgetHide(_window);
        _core?.OnWindowClosed();
        return true;
    }

    private bool OnDecidePolicy(nint webView, nint decision, int decisionType, nint data)
    {
        if (decisionType == _decisionNewWindow)
        {
            GtkNative.PolicyDecisionIgnore(decision);
            return true;
        }

        if (decisionType == _decisionNavigation)
        {
            nint action = GtkNative.NavigationPolicyDecisionGetNavigationAction(decision);
            nint request = GtkNative.NavigationActionGetRequest(action);
            string uri = Marshal.PtrToStringUTF8(GtkNative.UriRequestGetUri(request)) ?? string.Empty;
            if (!uri.StartsWith(_origin, StringComparison.Ordinal))
            {
                GtkNative.PolicyDecisionIgnore(decision);
                return true;
            }
        }

        return false;
    }

    private void OnWebProcessTerminated(nint webView, int reason, nint data) =>
        _core?.ReportFatal("web_process_terminated", $"The WebKit web process ended (reason {reason}).");

    private bool OnLoadFailed(nint webView, int loadEvent, nint failingUri, nint error, nint data)
    {
        string uri = Marshal.PtrToStringUTF8(failingUri) ?? string.Empty;
        _core?.ReportFatal("load_failed", $"WebKit could not load {uri}.");
        return true;
    }

    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".ttf" => "font/ttf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    };
}
