using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace S12;

/// <summary>Avalonia NativeControlHost that parents a WebView2 controller to a child HWND. Speaks the doc 57 section 4 bridge.</summary>
public sealed class MonacoHost : NativeControlHost
{
    public const string Origin = "https://athanor.invalid/";

    private nint _hwnd;
    private CoreWebView2Controller? _ctl;
    private int _outSeq;
    private int _inSeq;

    public int RejectedStaleSeq { get; private set; }

    public List<JsonObject> Received { get; } = [];

    public List<string> ExternalRequests { get; } = [];

    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CoreWebView2Environment? Env { get; private set; }

    public double ReadyMs { get; private set; }

    public double FirstPaintMs { get; private set; }

    public double WebViewCreatedMs { get; private set; }

    public event Action<JsonObject>? MessageReceived;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint h);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint h, out RECT r);

    private static double SinceStartMs() => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN
        _hwnd = CreateWindowExW(0, "Static", "", 0x40000000 | 0x10000000 | 0x02000000, 0, 0, 100, 100, parent.Handle, 0, 0, 0);
        _ = InitAsync();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _ctl?.Close();
        DestroyWindow(_hwnd);
    }

    private async Task InitAsync()
    {
        string udf = Path.Combine(Path.GetTempPath(), "S12-wv2-" + Environment.ProcessId);

        // A dead proxy makes any real network access fail loudly; every non-virtual-host request is also logged.
        var opts = new CoreWebView2EnvironmentOptions("--proxy-server=http://127.0.0.1:9");
        Env = await CoreWebView2Environment.CreateAsync(null, udf, opts);
        _ctl = await Env.CreateCoreWebView2ControllerAsync(_hwnd);
        WebViewCreatedMs = SinceStartMs();
        var core = _ctl.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("athanor.invalid", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            string u = e.Request.Uri;
            if (!u.StartsWith(Origin, StringComparison.Ordinal) && !u.StartsWith("data:", StringComparison.Ordinal) && !u.StartsWith("blob:", StringComparison.Ordinal))
            {
                ExternalRequests.Add(u);
            }
        };
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) => Received.Add(new JsonObject { ["type"] = "crashed", ["kind"] = e.ProcessFailedKind.ToString(), ["seq"] = 0 });
        Layout();
        core.Navigate(Origin + "host.html");
    }

    private void Layout()
    {
        if (_ctl is null || _hwnd == 0)
        {
            return;
        }

        GetClientRect(_hwnd, out var r);
        _ctl.Bounds = new System.Drawing.Rectangle(0, 0, r.R, r.B);
    }

    protected override Size ArrangeOverride(Size s)
    {
        var z = base.ArrangeOverride(s);
        Layout();
        return z;
    }

    private void OnMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var m = JsonNode.Parse(e.WebMessageAsJson)!.AsObject();
        int seq = m["seq"]?.GetValue<int>() ?? 0;
        if (seq <= _inSeq)
        {
            RejectedStaleSeq++; // seq guard, editor -> host
            return;
        }

        _inSeq = seq;
        string t = m["type"]!.GetValue<string>();
        if (t == "ready")
        {
            ReadyMs = SinceStartMs();
            Ready.TrySetResult();
        }

        if (t == "firstPaint")
        {
            FirstPaintMs = SinceStartMs();
        }

        Received.Add(m);
        MessageReceived?.Invoke(m);
    }

    public void Send(JsonObject m, int? forceSeq = null)
    {
        m["seq"] = forceSeq ?? ++_outSeq;
        _ctl!.CoreWebView2.PostWebMessageAsJson(m.ToJsonString());
    }

    public Task<string> EvalAsync(string js) => _ctl!.CoreWebView2.ExecuteScriptAsync(js);

    public Task<string> CdpAsync(string method, object p) => _ctl!.CoreWebView2.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(p));

    public void FocusWebView() => _ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int L;
        public int T;
        public int R;
        public int B;
    }
}
