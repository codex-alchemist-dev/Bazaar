using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace S13;

// Spike S13: Monaco in WebKitGTK driven from .NET through direct P/Invoke (no Avalonia in the loop; see docs/spikes/S13-webkitgtk.md).
internal static unsafe class Native
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Gobj = "libgobject-2.0.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Wk = "libwebkit2gtk-4.1.so.0";
    private const string Jsc = "libjavascriptcoregtk-4.1.so.0";

    [DllImport(Gtk)] public static extern bool gtk_init_check(ref int argc, nint argv);
    [DllImport(Gtk)] public static extern nint gtk_window_new(int type);
    [DllImport(Gtk)] public static extern void gtk_window_set_default_size(nint w, int x, int y);
    [DllImport(Gtk)] public static extern void gtk_window_present(nint w);
    [DllImport(Gtk)] public static extern nint gtk_box_new(int orientation, int spacing);
    [DllImport(Gtk)] public static extern void gtk_box_pack_start(nint box, nint child, bool expand, bool fill, uint pad);
    [DllImport(Gtk)] public static extern void gtk_container_add(nint c, nint w);
    [DllImport(Gtk)] public static extern nint gtk_entry_new();
    [DllImport(Gtk)] public static extern void gtk_widget_show_all(nint w);
    [DllImport(Gtk)] public static extern void gtk_widget_grab_focus(nint w);
    [DllImport(Gtk)] public static extern bool gtk_widget_has_focus(nint w);
    [DllImport(Gtk)] public static extern void gtk_main();
    [DllImport(Gtk)] public static extern void gtk_main_quit();
    [DllImport(Gtk)] public static extern nint gtk_clipboard_get(nint atom);
    [DllImport(Gtk)] public static extern void gtk_clipboard_set_text(nint cb, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int len);
    [DllImport(Gtk)] public static extern nint gtk_widget_get_toplevel(nint w);
    [DllImport("libgdk-3.so.0")] public static extern nint gdk_atom_intern([MarshalAs(UnmanagedType.LPUTF8Str)] string name, bool onlyIfExists);
    [DllImport(Gobj)] public static extern ulong g_signal_connect_data(nint inst, [MarshalAs(UnmanagedType.LPUTF8Str)] string sig, nint handler, nint data, nint destroy, int flags);
    [DllImport(Glib)] public static extern uint g_idle_add(nint fn, nint data);
    [DllImport(Glib)] public static extern uint g_timeout_add(uint ms, nint fn, nint data);
    [DllImport(Glib)] public static extern void g_free(nint p);
    [DllImport(Glib)] public static extern nint g_malloc(nuint n);
    [DllImport("libgio-2.0.so.0")] public static extern nint g_memory_input_stream_new_from_data(nint data, nint len, nint destroy);
    [DllImport(Wk)] public static extern nint webkit_user_content_manager_new();
    [DllImport(Wk)] public static extern bool webkit_user_content_manager_register_script_message_handler(nint m, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint world);
    [DllImport(Wk)] public static extern nint webkit_web_context_get_default();
    [DllImport(Wk)] public static extern nint webkit_web_context_get_security_manager(nint ctx);
    [DllImport(Wk)] public static extern void webkit_security_manager_register_uri_scheme_as_secure(nint sm, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme);
    [DllImport(Wk)] public static extern void webkit_security_manager_register_uri_scheme_as_cors_enabled(nint sm, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme);
    [DllImport(Wk)] public static extern void webkit_web_context_register_uri_scheme(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme, nint cb, nint data, nint destroy);
    [DllImport(Wk)] public static extern nint webkit_uri_scheme_request_get_path(nint req);
    [DllImport(Wk)] public static extern void webkit_uri_scheme_request_finish(nint req, nint stream, long len, [MarshalAs(UnmanagedType.LPUTF8Str)] string mime);
    [DllImport(Wk)] public static extern nint webkit_web_view_new_with_user_content_manager(nint ucm);
    [DllImport(Wk)] public static extern void webkit_web_view_load_uri(nint wv, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
    [DllImport(Wk)] public static extern void webkit_web_view_evaluate_javascript(nint wv, [MarshalAs(UnmanagedType.LPUTF8Str)] string script, nint len, nint world, nint source, nint cancel, nint cb, nint data);
    [DllImport(Wk)] public static extern void webkit_web_view_execute_editing_command(nint wv, [MarshalAs(UnmanagedType.LPUTF8Str)] string cmd);
    [DllImport(Wk)] public static extern uint webkit_web_view_get_web_process_identifier(nint wv);
    [DllImport(Wk)] public static extern nint webkit_get_major_version();
    [DllImport(Wk)] public static extern uint webkit_get_minor_version();
    [DllImport(Wk)] public static extern uint webkit_get_micro_version();
    [DllImport(Jsc)] public static extern nint jsc_value_to_string(nint v);
    [DllImport(Wk)] public static extern nint webkit_javascript_result_get_js_value(nint r);

    public delegate void ScriptMsgCb(nint ucm, nint jscValue, nint data);
    public delegate void SchemeCb(nint req, nint data);
    public delegate bool SourceFn(nint data);
    public delegate void FreeCb(nint p);
}

internal sealed class GtkContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _q = new();
    private readonly Native.SourceFn _fn;
    private readonly nint _fnPtr;

    public GtkContext()
    {
        _fn = _ =>
        {
            (SendOrPostCallback cb, object? st) item;
            lock (_q)
            {
                if (_q.Count == 0) return false;
                item = _q.Dequeue();
            }

            item.cb(item.st);
            return false;
        };
        _fnPtr = Marshal.GetFunctionPointerForDelegate(_fn);
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_q) _q.Enqueue((d, state));
        Native.g_idle_add(_fnPtr, 0);
    }
}

internal static class Program
{
    private static nint _win, _entry, _wv;
    private static int _outSeq, _inSeq, _rejectedStale, _evalId;
    private static readonly List<JsonObject> Received = [];
    private static readonly Dictionary<int, TaskCompletionSource<string>> Evals = [];
    private static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly List<string> Log = [];
    private static readonly Dictionary<string, object> R = [];
    private static readonly List<string> External = [];
    private static double _readyMs, _firstPaintMs;
    private static bool _handed;
    private static string _web = "";
    private static string _outPath = "results/linux-run.json";
    private static readonly Native.ScriptMsgCb ScriptCb = OnScript;
    private static readonly Native.SchemeCb SchemeCbDel = OnScheme;
    private static readonly List<object> Keep = [];

    private static double SinceStartMs() => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    private static int Main(string[] args)
    {
        _web = Path.Combine(AppContext.BaseDirectory, "web");
        _outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? _outPath;
        int argc = 0;
        if (!Native.gtk_init_check(ref argc, 0)) { Console.Error.WriteLine("gtk_init failed"); return 2; }
        SynchronizationContext.SetSynchronizationContext(new GtkContext());

        var ctx = Native.webkit_web_context_get_default();
        var sm = Native.webkit_web_context_get_security_manager(ctx);
        Native.webkit_security_manager_register_uri_scheme_as_secure(sm, "athanor");
        Native.webkit_security_manager_register_uri_scheme_as_cors_enabled(sm, "athanor");
        Native.webkit_web_context_register_uri_scheme(ctx, "athanor", Marshal.GetFunctionPointerForDelegate(SchemeCbDel), 0, 0);

        var ucm = Native.webkit_user_content_manager_new();
        Native.webkit_user_content_manager_register_script_message_handler(ucm, "bridge", 0);
        Native.g_signal_connect_data(ucm, "script-message-received::bridge", Marshal.GetFunctionPointerForDelegate(ScriptCb), 0, 0, 0);

        _win = Native.gtk_window_new(0);
        Native.gtk_window_set_default_size(_win, 1100, 700);
        var box = Native.gtk_box_new(1, 0);
        _entry = Native.gtk_entry_new();
        _wv = Native.webkit_web_view_new_with_user_content_manager(ucm);
        Native.gtk_box_pack_start(box, _entry, false, false, 0);
        Native.gtk_box_pack_start(box, _wv, true, true, 0);
        Native.gtk_container_add(_win, box);
        Native.gtk_widget_show_all(_win);
        Native.gtk_window_present(_win);
        Native.webkit_web_view_load_uri(_wv, "athanor://app/host.html");

        if (args.Contains("--auto"))
        {
            _ = Task.Run(() => { }).ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
            RunAuto().ContinueWith(_ => Native.gtk_main_quit(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        Native.gtk_main();
        return 0;
    }

    private static void OnScheme(nint req, nint data)
    {
        string path = Marshal.PtrToStringUTF8(Native.webkit_uri_scheme_request_get_path(req)) ?? "/";
        string file = Path.GetFullPath(Path.Combine(_web, path.TrimStart('/')));
        if (!file.StartsWith(_web, StringComparison.Ordinal) || !File.Exists(file)) { External.Add("404:" + path); file = Path.Combine(_web, "404.txt"); File.WriteAllText(file, "not found"); }
        byte[] bytes = File.ReadAllBytes(file);
        nint mem = Native.g_malloc((nuint)Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, mem, bytes.Length);
        nint stream = Native.g_memory_input_stream_new_from_data(mem, bytes.Length, FreeFnPtr);
        string mime = Path.GetExtension(file) switch
        {
            ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".json" => "application/json",
            ".ttf" => "font/ttf", ".svg" => "image/svg+xml", _ => "application/octet-stream",
        };
        Native.webkit_uri_scheme_request_finish(req, stream, bytes.Length, mime);
    }

    private static readonly Native.FreeCb FreeFn = Native.g_free;
    private static readonly nint FreeFnPtr = Marshal.GetFunctionPointerForDelegate(FreeFn);

    private static void OnScript(nint ucm, nint jscValue, nint data)
    {
        nint s = Native.jsc_value_to_string(Native.webkit_javascript_result_get_js_value(jscValue));
        string json = Marshal.PtrToStringUTF8(s)!;
        Native.g_free(s);
        var m = JsonNode.Parse(json)!.AsObject();
        int seq = m["seq"]?.GetValue<int>() ?? 0;
        if (seq <= _inSeq) { _rejectedStale++; return; }
        _inSeq = seq;
        string t = Ty(m);
        if (t == "ready") { _readyMs = SinceStartMs(); Ready.TrySetResult(); }
        if (t == "firstPaint") _firstPaintMs = SinceStartMs();
        if (t == "evalResult") { int id = m["id"]!.GetValue<int>(); if (Evals.Remove(id, out var tcs)) tcs.SetResult(m["value"]?.ToJsonString() ?? "null"); return; }
        if (t == "keyPressed") { Native.gtk_widget_grab_focus(_entry); _handed = true; }
        Received.Add(m);
    }

    private static string Ty(JsonObject m) => m["type"]!.GetValue<string>();

    private static JsonObject O(string type, params (string, JsonNode?)[] kv)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in kv) o[k] = v?.Parent is null ? v : v.DeepClone();
        return o;
    }

    private static void Send(JsonObject m, int? forceSeq = null)
    {
        m["seq"] = forceSeq ?? ++_outSeq;
        Native.webkit_web_view_evaluate_javascript(_wv, "window.__fromHost(" + m.ToJsonString() + ")", -1, 0, 0, 0, 0, 0);
    }

    private static Task<string> Eval(string expr)
    {
        int id = ++_evalId;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Evals[id] = tcs;
        Native.webkit_web_view_evaluate_javascript(_wv, $"window.__evalReply({id}, (function(){{ return {expr}; }})())", -1, 0, 0, 0, 0, 0);
        return tcs.Task;
    }

    private static async Task<string> Text(string doc)
    {
        int before = Received.Count;
        Send(O("getText", ("documentId", doc)));
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(30);
            var m = Received.Skip(before).FirstOrDefault(x => Ty(x) == "text");
            if (m != null) return m["text"]!.GetValue<string>();
        }

        return "<timeout>";
    }

    private static string Xdo(params string[] a)
    {
        var psi = new ProcessStartInfo("xdotool") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    private static void Rec(string k, object v) { R[k] = v; Log.Add($"{k} = {v}"); }
    private static void Check(string k, bool ok, string d = "") { R[k] = new { pass = ok, detail = d }; Log.Add($"{(ok ? "PASS" : "FAIL")} {k} {d}"); }

    private static long PssKb(int pid)
    {
        try
        {
            foreach (var l in File.ReadLines($"/proc/{pid}/smaps_rollup")) if (l.StartsWith("Pss:")) return long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        }
        catch (IOException) { }
        return 0;
    }

    private static async Task RunAuto()
    {
        try
        {
            await Task.WhenAny(Ready.Task, Task.Delay(30000));
            Check("loadOfflineFromBundle", Ready.Task.IsCompleted, $"ready={_readyMs:F0}ms since process start; scheme athanor:// (secure, cors-enabled); missing files={string.Join(",", External)}");
            if (!Ready.Task.IsCompleted) throw new InvalidOperationException("not ready");
            await Task.Delay(500);
            Rec("coldStartToMonacoReadyMs", Math.Round(_readyMs));
            Rec("coldStartToFirstPaintMs", Math.Round(_firstPaintMs));
            Rec("jsReadyMs", Math.Round(Received.First(x => Ty(x) == "ready")["tReadyMs"]!.GetValue<double>()));
            Rec("webkitgtk", $"{Native.webkit_get_major_version()}.{Native.webkit_get_minor_version()}.{Native.webkit_get_micro_version()}");
            string workerOk = await Eval("(()=>{try{const w=new Worker('monaco/vs/editor/editor.worker.js');w.terminate();return true}catch(e){return String(e)}})()");
            Check("webWorkerFromCustomScheme", workerOk == "true", workerOk);

            var theme = JsonNode.Parse(File.ReadAllText(Path.Combine(_web, "theme.dark.json")))!;
            Send(O("open", ("documentId", "doc1"), ("language", "csharp"), ("content", "class A { }\n")));
            Send(O("setTheme", ("theme", theme)));
            await Task.Delay(500);
            string bg = await Eval("__probe().bg");
            Check("themeApplied", bg.Contains("rgb(24, 24, 37)"), $"editor bg={bg} expected #181825, no reload");
            Send(O("setTheme", ("theme", JsonNode.Parse(File.ReadAllText(Path.Combine(_web, "theme.light.json")))!)));
            await Task.Delay(300);
            string bg2 = await Eval("__probe().bg");
            Check("themeSwapLive", bg2 != bg, $"after light theme bg={bg2}");
            Send(O("setTheme", ("theme", theme)));
            await Task.Delay(200);

            // typing through real X11 key events (xdotool/XTEST) into the focused GTK window
            Send(O("applyEdit", ("documentId", "doc1"), ("text", "")));
            Send(O("setClaimedKeys", ("keys", new JsonArray("Alt+KeyM", "F6"))));
            Send(O("focus"));
            Native.gtk_widget_grab_focus(_wv);
            string wid = Xdo("search", "--onlyvisible", "--class", "S13");
            if (wid == "") wid = Xdo("getactivewindow");
            Xdo("mousemove", "300", "300");
            Xdo("click", "1");
            await Task.Delay(300);
            Send(O("focus"));
            await Task.Delay(200);
            const string typed = "Hello, Bazaar! 123 x";
            Xdo("type", "--delay", "20", typed);
            string got = "";
            for (int i = 0; i < 40 && got.Length < typed.Length; i++)
            {
                await Task.Delay(250);
                got = await Text("doc1");
            }

            int mism = got.Length != typed.Length ? Math.Abs(got.Length - typed.Length) : got.Zip(typed).Count(p => p.First != p.Second);
            Check("typingAscii", mism == 0, $"typed='{typed}' got='{got.Replace("\n", "\\n")}' mismatches={mism} windowId={wid}");

            Rec("imeNote", "not tested: no input-method framework in Xvfb/WSLg session (ibus/fcitx). Needs a real desktop session.");

            const string paste = "paste: ünï©ödé 日本語 🚀";
            Send(O("applyEdit", ("documentId", "doc1"), ("text", "")));
            Send(O("focus"));
            Native.gtk_clipboard_set_text(Native.gtk_clipboard_get(Native.gdk_atom_intern("CLIPBOARD", false)), paste, -1);
            await Task.Delay(200);
            Xdo("key", "ctrl+v");
            await Task.Delay(500);
            string pg = await Text("doc1");
            if (pg != paste)
            {
                Send(O("applyEdit", ("documentId", "doc1"), ("text", "")));
                Native.webkit_web_view_execute_editing_command(_wv, "Paste");
                await Task.Delay(500);
                string pg2 = await Text("doc1");
                Log.Add($"ctrl+v via X11 gave '{pg}'; editing-command Paste gave '{pg2}'");
                pg = pg2;
            }

            Check("pasteNonAscii", pg == paste, $"got='{pg}'");

            Native.gtk_widget_grab_focus(_wv);
            Send(O("focus"));
            await Task.Delay(300);
            Xdo("key", "alt+m");
            await Task.Delay(500);
            Check("keyHandoffClaimedKey", _handed && Native.gtk_widget_has_focus(_entry), $"keyPressed -> GTK focus; handed={_handed}");
            string claimedList = await Eval("JSON.stringify(__probe().claimed)");
            Check("noCtrlChordsClaimed", !claimedList.Contains("Ctrl"), claimedList);
            Native.gtk_widget_grab_focus(_wv);
            await Task.Delay(100);
            int kp = Received.Count(x => Ty(x) == "keyPressed");
            Xdo("key", "ctrl+s");
            await Task.Delay(200);
            Check("unclaimedCtrlChordStaysInMonaco", Received.Count(x => Ty(x) == "keyPressed") == kp, "no keyPressed for Ctrl+S");

            int rej0 = _rejectedStale;
            Send(O("getText", ("documentId", "doc1")), 1);
            await Task.Delay(300);
            bool edRej = Received.Any(x => Ty(x) == "error" && (x["message"]?.GetValue<string>() ?? "").Contains("stale"));
            Send(O("testStaleOut"));
            await Task.Delay(300);
            Check("seqGuardBothWays", edRej && _rejectedStale == rej0 + 1, $"editor rejected stale host seq={edRej}; host rejected stale editor seq={_rejectedStale - rej0}");
            Send(O("bogusType"));
            await Task.Delay(200);
            Check("unknownTypeRejected", Received.Any(x => (x["message"]?.GetValue<string>() ?? "").StartsWith("unknown type")), "");

            Send(O("showDiff", ("original", "a\nb\nc\n"), ("modified", "a\nB\nc\nd\n"), ("language", "plaintext")));
            await Task.Delay(1500);
            string dp = await Eval("__probe().diffEditor");
            var dm = Received.LastOrDefault(x => Ty(x) == "diffComputed");
            Check("diffEditor", dp == "true" && dm != null, $"diffEditorDom={dp} changes={dm?["changes"]}");

            await Task.Delay(1500);
            int self = Environment.ProcessId;
            long total = PssKb(self);
            var parts = new List<string> { $"host:{PssKb(self) / 1024}MB" };
            long wvpss = 0;
            foreach (var d in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(d), out int pid)) continue;
                try
                {
                    string stat = File.ReadAllText($"{d}/stat");
                    var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (int.Parse(f[1]) == self)
                    {
                        string name = File.ReadAllText($"{d}/comm").Trim();
                        long p = PssKb(pid);
                        wvpss += p;
                        parts.Add($"{name}:{p / 1024}MB");
                    }
                }
                catch (IOException) { }
            }

            Rec("memHostProcessPssMB", total / 1024);
            Rec("memWebKitChildProcessesPssMB", wvpss / 1024);
            Rec("memProcesses", string.Join(" ", parts));
        }
        catch (Exception ex)
        {
            Rec("EXCEPTION", ex.ToString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_outPath))!);
        File.WriteAllText(_outPath, JsonSerializer.Serialize(new { results = R, log = Log }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
