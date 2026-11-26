namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// Hands a key to the client key router before the editor sees it (doc 57 §7). The client has no Ctrl chords (doc 41 §4.12,
/// doc 49), so a Ctrl key always stays with the editor and the router is not asked about it. A router that throws
/// is treated as not claiming the key, so a faulty router never swallows keys.
/// </summary>
public sealed class RouterKeyHandoff : IEditorKeyHandoff
{
    private readonly Func<EditorKeyEvent, bool> _clientClaims;

    public RouterKeyHandoff(Func<EditorKeyEvent, bool> clientClaims)
    {
        ArgumentNullException.ThrowIfNull(clientClaims);
        _clientClaims = clientClaims;
    }

    /// <summary>A handoff over the router's own key strokes: the event is converted in <see cref="EditorKeyCodes"/>, never twice.</summary>
    public static RouterKeyHandoff ForStrokes(Func<Bazaar.Client.Input.KeyStroke, bool> claims, IEnumerable<string>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var skip = new HashSet<string>(excluded ?? [], StringComparer.Ordinal);
        return new RouterKeyHandoff(key =>
            EditorKeyCodes.TryToChord(key, out var chord)
            && !skip.Contains(chord)
            && EditorKeyCodes.TryToKeyStroke(key, out var stroke)
            && claims(stroke));
    }

    /// <inheritdoc />
    public bool TryClaim(EditorKeyEvent key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Ctrl)
        {
            return false;
        }

        try
        {
            return _clientClaims(key);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
