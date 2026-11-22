namespace Bazaar.Server.Setup;

/// <summary>The outcome of reading one file of a template folder.</summary>
public enum TemplateFileStatus
{
    /// <summary>The file was read. The bytes are set.</summary>
    Found,

    /// <summary>The file does not exist in the template.</summary>
    Missing,

    /// <summary>The file is larger than the limit the caller passed. Nothing was read in full.</summary>
    TooLarge,

    /// <summary>The file is a link, or its path leaves the template folder. It is never followed.</summary>
    Rejected,
}

/// <summary>The result of <see cref="ITemplateSource.ReadAsync"/>. <see cref="Bytes"/> is set only when <see cref="Status"/> is Found.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Bytes">The file's exact bytes, or null.</param>
public sealed record TemplateFileRead(TemplateFileStatus Status, byte[]? Bytes);

/// <summary>
/// Reads the files of one template version (doc 45 section 1.8). Implementations: a file-system folder (the local folder source)
/// and, in a later slice, a git checkout. Paths are relative, forward-slash, and already checked by the loader; implementations
/// must still refuse anything that resolves outside the template.
/// </summary>
public interface ITemplateSource
{
    /// <summary>Reads <paramref name="relativePath"/> if it is at most <paramref name="maxBytes"/> long.</summary>
    Task<TemplateFileRead> ReadAsync(string relativePath, int maxBytes, CancellationToken cancellationToken = default);
}
