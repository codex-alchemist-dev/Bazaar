using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>A template folder held in memory, for tests. It applies the same size rule as the file-system source.</summary>
internal sealed class InMemoryTemplateSource : ITemplateSource
{
    private readonly Dictionary<string, byte[]> _files;

    public InMemoryTemplateSource(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            _files[path] = bytes;
        }
    }

    public Task<TemplateFileRead> ReadAsync(string relativePath, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(relativePath, out var bytes))
        {
            return Task.FromResult(new TemplateFileRead(TemplateFileStatus.Missing, null));
        }

        return Task.FromResult(bytes.Length > maxBytes
            ? new TemplateFileRead(TemplateFileStatus.TooLarge, null)
            : new TemplateFileRead(TemplateFileStatus.Found, (byte[])bytes.Clone()));
    }
}

/// <summary>A catalog held in memory: exact (id, version) pairs mapped to sources.</summary>
internal sealed class InMemoryTemplateCatalog : ITemplateCatalog
{
    private readonly Dictionary<(string Id, string Version), ITemplateSource> _entries = [];

    public InMemoryTemplateCatalog Add(string id, string version, ITemplateSource source)
    {
        _entries[(id, version)] = source;
        return this;
    }

    public Task<ITemplateSource?> FindAsync(string templateId, string version, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.TryGetValue((templateId, version), out var source) ? source : null);
}
