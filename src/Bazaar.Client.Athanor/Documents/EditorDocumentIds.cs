namespace Bazaar.Client.Athanor;

/// <summary>Builds <see cref="EditorDocumentId"/> values from client input, normalising the path first (doc 57 §5).</summary>
public static class EditorDocumentIds
{
    /// <summary>
    /// Returns the id for <paramref name="rawPath"/> inside <paramref name="workspaceId"/>.
    /// Throws <see cref="ArgumentException"/> when the workspace id is blank or the path fails <see cref="EditorPathNormalizer.TryNormalize"/>.
    /// </summary>
    public static EditorDocumentId Create(string workspaceId, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("A workspace id is required.", nameof(workspaceId));
        }

        if (rawPath is null || !EditorPathNormalizer.TryNormalize(rawPath, out var normalized))
        {
            throw new ArgumentException("The path is empty, escapes the workspace root, or is not a workspace-relative path.", nameof(rawPath));
        }

        return new EditorDocumentId(workspaceId, normalized);
    }
}
