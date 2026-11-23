using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace S12;

public sealed class MainWindow : Window
{
    private readonly MonacoHost _host = new() { MinHeight = 300 };
    private readonly TextBox _native = new() { PlaceholderText = "native Avalonia TextBox (key handoff target)" };
    private readonly Dictionary<string, object> _r = [];
    private readonly List<string> _log = [];

    public MainWindow()
    {
        Title = "S12 Monaco in WebView2 (Avalonia 12)";
        Width = 1100;
        Height = 700;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(_native);
        Grid.SetRow(_host, 1);
        grid.Children.Add(_host);
        Content = grid;
        Opened += async (_, _) =>
        {
            if (Program.Args.Contains("--auto"))
            {
                await RunAuto();
            }
        };
    }

    private static string Ty(JsonObject m) => m["type"]!.GetValue<string>();

    private static JsonObject O(string type, params (string, JsonNode?)[] kv)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in kv)
        {
            o[k] = v?.Parent is null ? v : v.DeepClone();
        }

        return o;
    }

    private void Rec(string k, object v)
    {
        _r[k] = v;
        _log.Add($"{k} = {v}");
    }

    private void Check(string k, bool ok, string detail = "")
    {
        _r[k] = new { pass = ok, detail };
        _log.Add($"{(ok ? "PASS" : "FAIL")} {k} {detail}");
    }

    private async Task Key(string code, string key, int vk, string? text = null, int mods = 0)
    {
        object P(string type) => new { type, key, code, windowsVirtualKeyCode = vk, modifiers = mods, text = text ?? "", unmodifiedText = text ?? "" };
        await _host.CdpAsync("Input.dispatchKeyEvent", P(text is null ? "rawKeyDown" : "keyDown"));
        await _host.CdpAsync("Input.dispatchKeyEvent", P("keyUp"));
    }

    private async Task<string> Text(string doc)
    {
        int before = _host.Received.Count;
        _host.Send(O("getText", ("documentId", doc)));
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(30);
            var m = _host.Received.Skip(before).FirstOrDefault(x => Ty(x) == "text");
            if (m != null)
            {
                return m["text"]!.GetValue<string>();
            }
        }

        return "<timeout>";
    }

    private async Task RunAuto()
    {
        string outPath = Program.Args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? "results\\run.json";
        try
        {
            await Task.WhenAny(_host.Ready.Task, Task.Delay(30000));
            Check("loadOfflineFromBundle", _host.Ready.Task.IsCompleted, $"ready={_host.ReadyMs:F0}ms since process start");
            if (!_host.Ready.Task.IsCompleted)
            {
                throw new InvalidOperationException("not ready");
            }

            await Task.Delay(500);
            Rec("coldStartToWebView2CreatedMs", Math.Round(_host.WebViewCreatedMs));
            Rec("coldStartToMonacoReadyMs", Math.Round(_host.ReadyMs));
            Rec("coldStartToFirstPaintMs", Math.Round(_host.FirstPaintMs));
            Rec("jsReadyMs", Math.Round(_host.Received.First(x => Ty(x) == "ready")["tReadyMs"]!.GetValue<double>()));
            Check("noExternalRequests", _host.ExternalRequests.Count == 0, string.Join(",", _host.ExternalRequests.Take(5)));

            var theme = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "web", "theme.dark.json")))!;
            _host.Send(O("open", ("documentId", "doc1"), ("language", "csharp"), ("content", "class A { }\n")));
            _host.Send(O("setTheme", ("theme", theme)));
            await Task.Delay(500);
            string bg = await _host.EvalAsync("JSON.stringify(__probe().bg)");
            Check("themeApplied", bg.Contains("rgb(24, 24, 37)"), $"editor bg={bg} expected #181825, no reload");
            var light = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "web", "theme.light.json")))!;
            _host.Send(O("setTheme", ("theme", light)));
            await Task.Delay(300);
            string bg2 = await _host.EvalAsync("JSON.stringify(__probe().bg)");
            Check("themeSwapLive", bg2 != bg, $"after light theme bg={bg2}");
            _host.Send(O("setTheme", ("theme", theme)));
            await Task.Delay(200);

            _host.Send(O("applyEdit", ("documentId", "doc1"), ("text", "")));
            _host.Send(O("setClaimedKeys", ("keys", new JsonArray("Alt+KeyM", "F6"))));
            _host.Send(O("focus"));
            _host.FocusWebView();
            await Task.Delay(300);
            const string typed = "Hello, Bazaar! 123 x";
            foreach (char c in typed)
            {
                await Key("Key" + char.ToUpper(c), c.ToString(), char.IsLetterOrDigit(c) ? char.ToUpper(c) : (c == (char)32 ? 32 : 0), c.ToString());
            }

            await Task.Delay(300);
            string got = await Text("doc1");
            int mism = got.Length != typed.Length ? Math.Abs(got.Length - typed.Length) : got.Zip(typed).Count(p => p.First != p.Second);
            Check("typingAscii", mism == 0, $"typed='{typed}' got='{got.Replace("\n", "\\n")}' mismatches={mism}");

            _host.Send(O("applyEdit", ("documentId", "doc1"), ("text", "")));
            _host.Send(O("focus"));
            await Task.Delay(200);
            await _host.CdpAsync("Input.imeSetComposition", new { text = "にほ", selectionStart = 2, selectionEnd = 2 });
            await Task.Delay(150);
            string mid = await _host.EvalAsync("document.querySelector('textarea.inputarea')?.value ?? ''");
            await _host.CdpAsync("Input.insertText", new { text = "日本" });
            await Task.Delay(200);
            string imeGot = await Text("doc1");
            Check("imeCommit_CdpSimulated", imeGot == "日本", $"textarea during composition={mid} committed='{imeGot}'");
            await _host.CdpAsync("Input.imeSetComposition", new { text = "かな", selectionStart = 2, selectionEnd = 2 });
            await _host.CdpAsync("Input.imeSetComposition", new { text = "", selectionStart = 0, selectionEnd = 0 });
            await Task.Delay(200);
            string cancel = await Text("doc1");
            Check("imeCancel_CdpSimulated", cancel == "日本", $"after cancel='{cancel}'");

            const string paste = "paste: ünï©ödé 日本語 🚀";
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(paste);
            _host.Send(O("applyEdit", ("documentId", "doc1"), ("text", "")));
            _host.Send(O("focus"));
            await Task.Delay(200);
            await _host.CdpAsync("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "v", code = "KeyV", windowsVirtualKeyCode = 86, modifiers = 2, commands = new[] { "paste" } });
            await _host.CdpAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "v", code = "KeyV", windowsVirtualKeyCode = 86, modifiers = 2 });
            await Task.Delay(400);
            string pg = await Text("doc1");
            Check("pasteNonAscii", pg == paste, $"got='{pg}'");

            _host.FocusWebView();
            _host.Send(O("focus"));
            await Task.Delay(200);
            bool handed = false;
            _host.MessageReceived += m =>
            {
                if (Ty(m) == "keyPressed")
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _native.Focus();
                        handed = true;
                    });
                }
            };
            await Key("KeyM", "m", 77, null, 1);
            await Task.Delay(500);
            Check("keyHandoffClaimedKey", handed && _native.IsFocused, $"keyPressed -> Avalonia focus; handed={handed} nativeFocused={_native.IsFocused}");
            string claimedList = await _host.EvalAsync("JSON.stringify(__probe().claimed)");
            Check("noCtrlChordsClaimed", !claimedList.Contains("Ctrl"), claimedList);
            _host.FocusWebView();
            await Task.Delay(100);
            int kp = _host.Received.Count(x => Ty(x) == "keyPressed");
            await Key("KeyS", "s", 83, null, 2);
            await Task.Delay(200);
            Check("unclaimedCtrlChordStaysInMonaco", _host.Received.Count(x => Ty(x) == "keyPressed") == kp, "no keyPressed for Ctrl+S");

            int rej0 = _host.RejectedStaleSeq;
            _host.Send(O("getText", ("documentId", "doc1")), forceSeq: 1);
            await Task.Delay(300);
            bool editorRejected = _host.Received.Any(x => Ty(x) == "error" && (x["message"]?.GetValue<string>() ?? "").Contains("stale"));
            _host.Send(O("testStaleOut"));
            await Task.Delay(300);
            Check("seqGuardBothWays", editorRejected && _host.RejectedStaleSeq == rej0 + 1, $"editor rejected stale host seq={editorRejected}; host rejected stale editor seq={_host.RejectedStaleSeq - rej0}");
            _host.Send(O("bogusType"));
            await Task.Delay(200);
            Check("unknownTypeRejected", _host.Received.Any(x => (x["message"]?.GetValue<string>() ?? "").StartsWith("unknown type")), "");

            _host.Send(O("showDiff", ("original", "a\nb\nc\n"), ("modified", "a\nB\nc\nd\n"), ("language", "plaintext")));
            await Task.Delay(1500);
            string dp = await _host.EvalAsync("JSON.stringify(__probe().diffEditor)");
            var dm = _host.Received.LastOrDefault(x => Ty(x) == "diffComputed");
            Check("diffEditor", dp.Contains("true") && dm != null, $"diffEditorDom={dp} changes={dm?["changes"]}");

            await Task.Delay(1500);
            long privWv = 0;
            var kinds = new List<string>();
            foreach (var pi in _host.Env!.GetProcessInfos())
            {
                try
                {
                    using var p = Process.GetProcessById(pi.ProcessId);
                    privWv += p.PrivateMemorySize64;
                    kinds.Add($"{pi.Kind}:{p.PrivateMemorySize64 / 1048576}MB");
                }
                catch (ArgumentException)
                {
                }
            }

            Rec("memAvaloniaProcessPrivateMB", Process.GetCurrentProcess().PrivateMemorySize64 / 1048576);
            Rec("memWebView2ProcessesPrivateMB", privWv / 1048576);
            Rec("memWebView2Processes", string.Join(" ", kinds));
            Rec("webview2RuntimeVersion", _host.Env!.BrowserVersionString);
            Rec("crashes", _host.Received.Count(x => Ty(x) == "crashed"));
        }
        catch (Exception ex)
        {
            Rec("EXCEPTION", ex.ToString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { results = _r, log = _log }, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }
}
