using System.Runtime.InteropServices;

namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>
/// The hand-written P/Invoke surface for GTK 3 and WebKitGTK 4.1 (from spike S13, docs/spikes/S13-webkitgtk.md). The
/// libraries are loaded the first time a function is called, so this assembly builds and starts without GTK installed.
/// A GTK or WebKit major change breaks this silently, so the surface is kept as small as it can be.
/// </summary>
internal static partial class GtkNative
{
    public const string GtkLib = "libgtk-3.so.0";
    public const string GdkLib = "libgdk-3.so.0";
    public const string GobjectLib = "libgobject-2.0.so.0";
    public const string GlibLib = "libglib-2.0.so.0";
    public const string GioLib = "libgio-2.0.so.0";
    public const string WebKitLib = "libwebkit2gtk-4.1.so.0";
    public const string JscLib = "libjavascriptcoregtk-4.1.so.0";
    public const string LibC = "libc.so.6";

    /// <summary>The libraries the helper needs. A missing one ends the helper with <see cref="HelperExitCodes.NativeLibraryMissing"/>.</summary>
    public static readonly string[] RequiredLibraries = [GtkLib, GdkLib, GobjectLib, GlibLib, GioLib, WebKitLib, JscLib];

    public delegate void ScriptMessageCallback(nint contentManager, nint jscValue, nint data);

    public delegate void SchemeRequestCallback(nint request, nint data);

    public delegate bool IdleCallback(nint data);

    public delegate bool DeleteEventCallback(nint widget, nint gdkEvent, nint data);

    public delegate bool DecidePolicyCallback(nint webView, nint decision, int decisionType, nint data);

    public delegate void WebProcessTerminatedCallback(nint webView, int reason, nint data);

    public delegate bool LoadFailedCallback(nint webView, int loadEvent, nint failingUri, nint error, nint data);

    /// <summary>True when every required library can be loaded.</summary>
    public static bool TryLoadAll(out string? missing)
    {
        foreach (string library in RequiredLibraries)
        {
            if (!NativeLibrary.TryLoad(library, out nint handle))
            {
                missing = library;
                return false;
            }

            NativeLibrary.Free(handle);
        }

        missing = null;
        return true;
    }

    /// <summary>The address of <c>g_free</c>, used as the destroy notifier of memory handed to GLib.</summary>
    public static nint GFreeAddress()
    {
        nint lib = NativeLibrary.Load(GlibLib);
        return NativeLibrary.GetExport(lib, "g_free");
    }

    [LibraryImport(LibC, EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetEnv(string name, string value, int overwrite);

    [LibraryImport(GtkLib, EntryPoint = "gtk_init_check")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GtkInitCheck(ref int argc, nint argv);

    [LibraryImport(GtkLib, EntryPoint = "gtk_window_new")]
    public static partial nint WindowNew(int type);

    [LibraryImport(GtkLib, EntryPoint = "gtk_window_set_default_size")]
    public static partial void WindowSetDefaultSize(nint window, int width, int height);

    [LibraryImport(GtkLib, EntryPoint = "gtk_window_resize")]
    public static partial void WindowResize(nint window, int width, int height);

    [LibraryImport(GtkLib, EntryPoint = "gtk_window_move")]
    public static partial void WindowMove(nint window, int x, int y);

    [LibraryImport(GtkLib, EntryPoint = "gtk_window_set_title", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WindowSetTitle(nint window, string title);

    [LibraryImport(GtkLib, EntryPoint = "gtk_window_present")]
    public static partial void WindowPresent(nint window);

    [LibraryImport(GtkLib, EntryPoint = "gtk_container_add")]
    public static partial void ContainerAdd(nint container, nint widget);

    [LibraryImport(GtkLib, EntryPoint = "gtk_widget_show_all")]
    public static partial void WidgetShowAll(nint widget);

    [LibraryImport(GtkLib, EntryPoint = "gtk_widget_hide")]
    public static partial void WidgetHide(nint widget);

    [LibraryImport(GtkLib, EntryPoint = "gtk_widget_grab_focus")]
    public static partial void WidgetGrabFocus(nint widget);

    [LibraryImport(GtkLib, EntryPoint = "gtk_main")]
    public static partial void RunMain();

    [LibraryImport(GtkLib, EntryPoint = "gtk_main_quit")]
    public static partial void MainQuit();

    [LibraryImport(GobjectLib, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

    [LibraryImport(GlibLib, EntryPoint = "g_idle_add")]
    public static partial uint IdleAdd(nint function, nint data);

    [LibraryImport(GlibLib, EntryPoint = "g_free")]
    public static partial void Free(nint pointer);

    [LibraryImport(GlibLib, EntryPoint = "g_malloc")]
    public static partial nint Malloc(nuint size);

    [LibraryImport(GioLib, EntryPoint = "g_memory_input_stream_new_from_data")]
    public static partial nint MemoryInputStreamNewFromData(nint data, nint length, nint destroy);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_user_content_manager_new")]
    public static partial nint UserContentManagerNew();

    [LibraryImport(WebKitLib, EntryPoint = "webkit_user_content_manager_register_script_message_handler", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UserContentManagerRegisterScriptMessageHandler(nint manager, string name, nint world);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_web_context_get_default")]
    public static partial nint WebContextGetDefault();

    [LibraryImport(WebKitLib, EntryPoint = "webkit_web_context_get_security_manager")]
    public static partial nint WebContextGetSecurityManager(nint context);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_security_manager_register_uri_scheme_as_secure", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SecurityManagerRegisterSchemeAsSecure(nint manager, string scheme);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_security_manager_register_uri_scheme_as_cors_enabled", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SecurityManagerRegisterSchemeAsCorsEnabled(nint manager, string scheme);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_web_context_register_uri_scheme", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WebContextRegisterUriScheme(nint context, string scheme, nint callback, nint data, nint destroy);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_uri_scheme_request_get_path")]
    public static partial nint UriSchemeRequestGetPath(nint request);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_uri_scheme_request_finish", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void UriSchemeRequestFinish(nint request, nint stream, long length, string mimeType);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_web_view_new_with_user_content_manager")]
    public static partial nint WebViewNewWithUserContentManager(nint manager);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_web_view_load_uri", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WebViewLoadUri(nint webView, string uri);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_web_view_evaluate_javascript", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void WebViewEvaluateJavascript(nint webView, string script, nint length, nint world, nint sourceUri, nint cancellable, nint callback, nint data);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_javascript_result_get_js_value")]
    public static partial nint JavascriptResultGetJsValue(nint result);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_navigation_policy_decision_get_navigation_action")]
    public static partial nint NavigationPolicyDecisionGetNavigationAction(nint decision);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_navigation_action_get_request")]
    public static partial nint NavigationActionGetRequest(nint action);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_uri_request_get_uri")]
    public static partial nint UriRequestGetUri(nint request);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_policy_decision_ignore")]
    public static partial void PolicyDecisionIgnore(nint decision);

    [LibraryImport(WebKitLib, EntryPoint = "webkit_get_major_version")]
    public static partial uint GetMajorVersion();

    [LibraryImport(WebKitLib, EntryPoint = "webkit_get_minor_version")]
    public static partial uint GetMinorVersion();

    [LibraryImport(WebKitLib, EntryPoint = "webkit_get_micro_version")]
    public static partial uint GetMicroVersion();

    [LibraryImport(JscLib, EntryPoint = "jsc_value_to_string")]
    public static partial nint JscValueToString(nint value);
}
