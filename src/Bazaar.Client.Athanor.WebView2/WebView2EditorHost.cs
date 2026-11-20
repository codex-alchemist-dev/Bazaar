using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Bazaar.Client.Athanor.Hosting;

namespace Bazaar.Client.Athanor.WebView2;

/// <summary>
/// The Windows <see cref="IEditorHost"/> for Monaco in a WebView2 island (doc 57 §3, §3a). An Avalonia
/// <see cref="NativeControlHost"/> that creates a child window and parents the WebView2 controller to it. Everything about
/// the bridge is in <see cref="BridgeEditorHost"/>; the window plumbing is in <see cref="WebView2EditorSurface"/>.
/// </summary>
/// <remarks>
/// Child-window hosting means Avalonia content cannot draw over the editor rectangle (doc 57 §3a). A key the client router
/// claims moves native focus back to the top-level window, so the router's target receives the next key.
/// </remarks>
public sealed class WebView2EditorHost : NativeControlHost, IEditorHost
{
    private readonly WebView2EditorSurface _surface;
    private nint _window;

    /// <param name="options">Where the profile lives and which bundle to load.</param>
    /// <param name="handoff">The client key router. Claimed keys never reach <see cref="KeyPressed"/>.</param>
    /// <param name="claims">The router's claimed-key table, pushed to the page (doc 57 §7).</param>
    /// <param name="theme">When set, its payload is pushed as soon as the page is ready, without a reload.</param>
    public WebView2EditorHost(
        WebView2HostOptions options,
        IEditorKeyHandoff? handoff = null,
        IClaimedKeySource? claims = null,
        EditorThemeProvider? theme = null)
    {
        _surface = new WebView2EditorSurface(options, handoff, claims);
        _surface.Host.KeyClaimed += OnKeyClaimed;
        if (theme is not null)
        {
            _ = theme.PushAsync(_surface.Host, CancellationToken.None); // queued until the page is ready
        }
    }

    public event EventHandler<EditorChange>? Changed
    {
        add => _surface.Host.Changed += value;
        remove => _surface.Host.Changed -= value;
    }

    public event EventHandler<EditorDocumentId>? SaveRequested
    {
        add => _surface.Host.SaveRequested += value;
        remove => _surface.Host.SaveRequested -= value;
    }

    public event EventHandler<EditorKeyEvent>? KeyPressed
    {
        add => _surface.Host.KeyPressed += value;
        remove => _surface.Host.KeyPressed -= value;
    }

    public event EventHandler? Ready
    {
        add => _surface.Host.Ready += value;
        remove => _surface.Host.Ready -= value;
    }

    public event EventHandler<EditorCrashReason>? Crashed
    {
        add => _surface.Host.Crashed += value;
        remove => _surface.Host.Crashed -= value;
    }

    /// <summary>The surface behind this control, for diagnostics.</summary>
    public WebView2EditorSurface Surface => _surface;

    /// <summary>Why the editor could not start, or null. Check it after <see cref="Crashed"/> to choose the fallback host.</summary>
    public string? FailureMessage => _surface.FailureMessage;

    public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken) => _surface.Host.OpenAsync(document, cancellationToken);

    public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken) => _surface.Host.CloseAsync(id, cancellationToken);

    public Task FocusAsync(CancellationToken cancellationToken)
    {
        _surface.MoveFocusIntoEditor();
        return _surface.Host.FocusAsync(cancellationToken);
    }

    public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken) => _surface.Host.SetThemeAsync(monacoThemeJson, cancellationToken);

    public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) => _surface.Host.SetReadOnlyAsync(id, isReadOnly, cancellationToken);

    public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken) => _surface.Host.ShowDiffAsync(original, modified, languageId, cancellationToken);

    public ValueTask DisposeAsync() => _surface.DisposeAsync();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _window = NativeMethods.CreateWindowEx(
            0, "Static", string.Empty, NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.WsClipChildren, 0, 0, 100, 100, parent.Handle, 0, 0, 0);
        if (_window == 0)
        {
            throw new InvalidOperationException("The editor window could not be created.");
        }

        _ = AttachAsync();
        return new PlatformHandle(_window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _ = _surface.DisposeAsync().AsTask();
        NativeMethods.DestroyWindow(_window);
        _window = 0;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);
        ResizeSurface();
        return size;
    }

    private async Task AttachAsync()
    {
        await _surface.AttachAsync(_window).ConfigureAwait(true);
        ResizeSurface();
    }

    private void ResizeSurface()
    {
        if (_window != 0 && NativeMethods.GetClientRect(_window, out NativeMethods.Rect rect))
        {
            _surface.SetBounds(rect.Right, rect.Bottom);
        }
    }

    // The router has acted on a claimed key. Native focus is still inside the WebView2 window, so give it back to the
    // top-level window and re-assert the element the router focused, otherwise the next key would go to the editor.
    private void OnKeyClaimed(object? sender, EditorKeyEvent key) => Dispatcher.UIThread.Post(() =>
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        nint topHandle = top?.TryGetPlatformHandle()?.Handle ?? 0;
        if (top is null || topHandle == 0)
        {
            return;
        }

        var focused = top.FocusManager?.GetFocusedElement();
        NativeMethods.SetFocus(topHandle);
        focused?.Focus();
    });
}
