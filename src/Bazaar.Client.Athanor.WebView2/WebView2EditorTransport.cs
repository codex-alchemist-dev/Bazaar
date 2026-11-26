using Bazaar.Client.Athanor.Hosting;
using Microsoft.Web.WebView2.Core;

namespace Bazaar.Client.Athanor.WebView2;

/// <summary>What a WebView2 process failure means for the editor (doc 57 §1, §3a).</summary>
/// <param name="Reason">The crash reason to report, or null when the failure does not stop the editor (a GPU or utility process restarting).</param>
/// <param name="CanReload">True when reloading the page recovers it; false when the whole browser process is gone and the environment must be recreated.</param>
public readonly record struct ProcessFailureOutcome(EditorCrashReason? Reason, bool CanReload);

/// <summary>
/// The <see cref="IEditorBridgeTransport"/> over one <see cref="CoreWebView2"/>. Text goes in and out as web messages
/// (<c>PostWebMessageAsString</c> and <c>TryGetWebMessageAsString</c>), never as script, so document content is data.
/// The core is attached after the environment exists; sends before that fail, but the bridge host only sends after the
/// page reports ready.
/// </summary>
public sealed class WebView2EditorTransport : IEditorBridgeTransport
{
    private CoreWebView2? _core;
    private bool _disposed;

    public event EventHandler<string>? MessageReceived;

    public event EventHandler<EditorCrashReason>? Faulted;

    /// <summary>Maps a WebView2 failure to the editor crash reason and says whether a reload recovers it.</summary>
    public static ProcessFailureOutcome MapProcessFailure(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            or CoreWebView2ProcessFailedKind.FrameRenderProcessExited => new ProcessFailureOutcome(EditorCrashReason.RendererExited, true),
        CoreWebView2ProcessFailedKind.BrowserProcessExited => new ProcessFailureOutcome(EditorCrashReason.RendererExited, false),
        CoreWebView2ProcessFailedKind.UnknownProcessExited => new ProcessFailureOutcome(EditorCrashReason.Unknown, true),
        _ => new ProcessFailureOutcome(null, false), // GPU, utility, sandbox helper, PPAPI: WebView2 restarts these itself
    };

    /// <summary>Connects the transport to a live core. Called once, on the UI thread.</summary>
    public void Attach(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (_core is not null)
        {
            throw new InvalidOperationException("The transport is already attached.");
        }

        _core = core;
        core.WebMessageReceived += OnWebMessage;
    }

    /// <summary>Raises <see cref="Faulted"/> from a failure the surface saw (a process failure, or a page that could not load).</summary>
    public void ReportFault(EditorCrashReason reason)
    {
        if (!_disposed)
        {
            Faulted?.Invoke(this, reason);
        }
    }

    public ValueTask PostAsync(string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        ObjectDisposedException.ThrowIf(_disposed, this);
        CoreWebView2 core = _core ?? throw new InvalidOperationException("The editor page is not attached yet.");
        core.PostWebMessageAsString(json);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessage;
        }

        return ValueTask.CompletedTask;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only a page on our own origin may talk to the host, and only with a string message.
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out Uri? source) || source.Host != WebView2HostOptions.VirtualHost)
        {
            return;
        }

        string? text;
        try
        {
            text = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return; // not a string message
        }

        if (text is not null)
        {
            MessageReceived?.Invoke(this, text);
        }
    }
}
