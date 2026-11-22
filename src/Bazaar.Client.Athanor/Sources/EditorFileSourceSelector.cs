namespace Bazaar.Client.Athanor.Sources;

/// <summary>
/// Chooses the file source for a head (doc 57 §2.2): the server-backed source when the head has a server connection,
/// otherwise the offline source. A factory that throws counts as no connection, so the editor still opens.
/// </summary>
public static class EditorFileSourceSelector
{
    /// <param name="connected">Builds the server-backed source, or returns null when the head has no connection.</param>
    /// <param name="report">Receives one line when the connected source could not be built.</param>
    public static IEditorFileSource Select(Func<IEditorFileSource?>? connected, Action<string>? report = null)
    {
        if (connected is not null)
        {
            try
            {
                if (connected() is { } source)
                {
                    return source;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report?.Invoke($"Athanor: the server file source could not be built ({ex.Message}); the editor is offline.");
            }
        }

        return new OfflineEditorFileSource();
    }
}
