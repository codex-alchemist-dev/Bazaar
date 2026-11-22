namespace Bazaar.Server.Setup;

/// <summary>
/// An <see cref="ITemplateSource"/> over one template folder on disk (doc 45 section 1.8). A path that resolves outside the folder,
/// and a link of any kind, is reported as Rejected and is never followed. The size is checked before the file is read in full.
/// </summary>
/// <remarks>
/// Checked on Windows and Linux alike. Path comparison is case-insensitive on Windows, where paths are case-insensitive.
/// </remarks>
public sealed class FileSystemTemplateSource : ITemplateSource
{
    private readonly string _root;
    private readonly StringComparison _comparison;

    /// <param name="rootDirectory">The template folder (the one that holds <c>template.json</c>).</param>
    public FileSystemTemplateSource(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <inheritdoc />
    public async Task<TemplateFileRead> ReadAsync(string relativePath, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, _comparison))
        {
            return new TemplateFileRead(TemplateFileStatus.Rejected, null);
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return new TemplateFileRead(TemplateFileStatus.Missing, null);
        }

        if (info.LinkTarget is not null)
        {
            return new TemplateFileRead(TemplateFileStatus.Rejected, null);
        }

        if (info.Length > maxBytes)
        {
            return new TemplateFileRead(TemplateFileStatus.TooLarge, null);
        }

        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        return bytes.Length > maxBytes
            ? new TemplateFileRead(TemplateFileStatus.TooLarge, null)
            : new TemplateFileRead(TemplateFileStatus.Found, bytes);
    }
}
