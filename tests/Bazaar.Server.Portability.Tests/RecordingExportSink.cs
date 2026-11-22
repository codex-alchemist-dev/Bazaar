using System.Text;

namespace Bazaar.Server.Portability.Tests;

/// <summary>An export sink that keeps every file in memory, so a test can read the exact bytes an exporter wrote.</summary>
internal sealed class RecordingExportSink : IExportFileSink
{
    private readonly Dictionary<string, MemoryStream> _files = new(StringComparer.Ordinal);

    /// <summary>The paths opened, in ordinal order.</summary>
    public IReadOnlyList<string> Paths => _files.Keys.Order(StringComparer.Ordinal).ToList();

    public Task<Stream> OpenAsync(string path, CancellationToken ct)
    {
        if (_files.ContainsKey(path))
        {
            throw new InvalidOperationException("Opened twice: " + path);
        }

        var stream = new MemoryStream();
        _files[path] = stream;
        return Task.FromResult<Stream>(stream);
    }

    public byte[] Bytes(string path) => _files[path].ToArray();

    public string Text(string path) => Encoding.UTF8.GetString(Bytes(path));

    /// <summary>The non-empty lines of a file, without the line feeds.</summary>
    public string[] Lines(string path) => Text(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
