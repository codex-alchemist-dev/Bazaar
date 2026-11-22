using System.Security.Cryptography;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// Writes one <c>gc.jsonl.v1</c> file as a sealed hash chain and hashes its bytes as they go out (doc 44 section 6.2). Each line is
/// streamed when it is written, so a file is never held whole in memory.
/// </summary>
internal sealed class GcJsonlFileWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly Stream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes;
    private bool _closed;

    private GcJsonlFileWriter(string path, Stream stream)
    {
        _path = path;
        _stream = stream;
    }

    /// <summary>The <c>hash</c> of the last line written, or the genesis hash for an empty file.</summary>
    public string PrevHash { get; private set; } = GcJsonlFormat.GenesisHash;

    /// <summary>Lines written so far.</summary>
    public long Count { get; private set; }

    public static async Task<GcJsonlFileWriter> CreateAsync(IExportFileStore store, string path, CancellationToken ct) =>
        new(path, await store.CreateAsync(path, ct));

    /// <summary>Seals and writes one line. The fields are written by <paramref name="writeFields"/>; the chain is kept here.</summary>
    public async Task WriteLineAsync(Action<Utf8JsonWriter> writeFields, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var line = GcJsonlFormat.Seal(PrevHash, writeFields);
        await _stream.WriteAsync(line.Bytes, ct);
        _hash.AppendData(line.Bytes);
        _bytes += line.Bytes.Length;
        PrevHash = line.Hash;
        Count++;
    }

    /// <summary>Flushes and closes the file, and returns its name, SHA-256 and size. Call once, after the last line.</summary>
    public async Task<ExportedFile> FinishAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        await _stream.FlushAsync(ct);
        var file = new ExportedFile(_path, Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant(), _bytes);
        await DisposeAsync();
        return file;
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _hash.Dispose();
        await _stream.DisposeAsync();
    }
}
