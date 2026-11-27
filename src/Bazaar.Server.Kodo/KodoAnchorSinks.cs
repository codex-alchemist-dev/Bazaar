using System.Text;
using Bazaar.Kodo.Anchoring;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The default sink: it keeps nothing and never accepts, so a checkpoint stays unanchored and retention keeps its tail
/// (doc 46 section 6.5.2). Used only when the operator has configured no sink.
/// </summary>
internal sealed class NoOpAnchorSink : IAnchorSink
{
    public string Id => "none";

    public ValueTask<bool> PublishAsync(AnchorRecord anchor, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>
/// Appends each anchor as one canonical JSON line to a local file. The file is append-only for this process, but it sits on the
/// operator's own disk, so it is a record for local audit and a hand-off point, not external tamper evidence (doc 46 section 6.5.2).
/// </summary>
internal sealed class FileAnchorSink : IAnchorSink, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAnchorSink(string path, string id = "file")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _path = path;
        Id = id;
    }

    public string Id { get; }

    public void Dispose() => _gate.Dispose();

    public async ValueTask<bool> PublishAsync(AnchorRecord anchor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        byte[] line = Encoding.UTF8.GetBytes(AnchorCodec.SerializeToString(anchor) + "\n");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            await using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
