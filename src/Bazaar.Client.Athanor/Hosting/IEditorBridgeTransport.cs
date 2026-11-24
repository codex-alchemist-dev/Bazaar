namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// The message channel under a <see cref="BridgeEditorHost"/> (doc 57 §4): one JSON text per message in each direction.
/// A WebView2 control, a WebKitGTK view or a test fake supplies it. The transport only moves text; it does not decode it.
/// </summary>
public interface IEditorBridgeTransport : IAsyncDisposable
{
    /// <summary>Raised for every JSON text the editor page posts. Called on the thread the transport owns.</summary>
    event EventHandler<string>? MessageReceived;

    /// <summary>Raised when the page or its renderer process fails (it exited, hung or could not start).</summary>
    event EventHandler<EditorCrashReason>? Faulted;

    /// <summary>Delivers one JSON text to the page as a message (never as script).</summary>
    ValueTask PostAsync(string json, CancellationToken cancellationToken);
}

/// <summary>
/// The keys the client key router claims, as chords (doc 57 §3a, §7). Supplied by the client's key router, because the
/// editor page has to decide a key at once and cannot wait for the host.
/// </summary>
public interface IClaimedKeySource
{
    /// <summary>Chords in the form <c>Alt+Meta+Shift+Code</c> (modifiers in that order, then <c>KeyboardEvent.code</c>). Never a Ctrl chord.</summary>
    IReadOnlyList<string> Chords { get; }

    /// <summary>Raised when <see cref="Chords"/> changed, so the host pushes the table again.</summary>
    event EventHandler? Changed;
}
