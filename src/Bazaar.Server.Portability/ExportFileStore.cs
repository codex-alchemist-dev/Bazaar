namespace Bazaar.Server.Portability;

/// <summary>A file an export wrote: its path inside the export, its SHA-256 (lowercase hex) and its size in bytes.</summary>
public sealed record ExportedFile(string Path, string Sha256, long Bytes);

/// <summary>
/// Where a streamed export writes its files and reads them back. The host implements it over the export staging area; tests use
/// memory. Paths are relative and use forward slashes, and the exporter chooses them. A file is created once, written, then read.
/// </summary>
public interface IExportFileStore
{
    /// <summary>Creates a new file for writing. Throws if the path already exists. The caller disposes the stream.</summary>
    Task<Stream> CreateAsync(string path, CancellationToken ct);

    /// <summary>Opens an existing file for reading. Throws <see cref="FileNotFoundException"/> when the file is absent.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
}
