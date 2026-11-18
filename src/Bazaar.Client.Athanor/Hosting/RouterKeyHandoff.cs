namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// Hands a key to the client key router before the editor sees it (doc 52 §7). The client has no Ctrl chords (doc 41 §4.12,
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
