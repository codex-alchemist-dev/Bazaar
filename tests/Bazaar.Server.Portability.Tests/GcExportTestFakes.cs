using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Portability.Tests;

/// <summary>Builders for GC events and sealed versions. Ids are fixed, so the exports are reproducible.</summary>
internal static class GcFixtures
{
    public static readonly Guid GcId = Id(1);
    public static readonly GcPrincipal Owner = new(Id(2), PrincipalKind.Owner, "Owner");
    public static readonly Guid Key = Id(3);

    private static readonly byte[] _nonce = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    public static Guid Id(long n) => Guid.Parse("00000000-0000-0000-0000-" + n.ToString("D12", CultureInfo.InvariantCulture));

    public static Guid MessageId(long n) => Id(100 + n);

    public static GcEvent Event(
        long seq, string kind, Guid? messageId = null, int? version = null, string? payload = null) =>
        new(GcId, seq, Id(1000 + seq), kind, AuthorKind.Human, Owner.Id, SourceTrust.MemberHuman, false, null,
            messageId, version, null, null, payload, At(seq));

    /// <summary>A message or edit event for <paramref name="messageId"/> at version <paramref name="version"/>.</summary>
    public static GcEvent MessageEvent(long seq, long messageId, int version) =>
        Event(seq, version == 1 ? GcEventKinds.Message : GcEventKinds.MessageEdit, MessageId(messageId), version);

    /// <summary>A version row whose body is the plaintext XOR-ed with a fixed byte. Only the export sees the ciphertext.</summary>
    public static GcMessageVersionRecord Sealed(long seq, long messageId, int version, string plaintext) =>
        new(seq, MessageId(messageId), version, Owner.Id, At(seq), false, Key, _nonce, Seal(plaintext));

    public static GcMessageVersionRecord Tombstone(long seq, long messageId, int version) =>
        new(seq, MessageId(messageId), version, Owner.Id, At(seq), true, null, null, null);

    public static byte[] Seal(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0x5A;
        }

        return bytes;
    }

    private static DateTimeOffset At(long seq) => new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero).AddSeconds(seq);
}

/// <summary>An in-memory GC log. It pages like the real reader and can be given a head or a narrower grant for a test.</summary>
internal sealed class FakeGcLog : IGcLogReader
{
    public List<GcEvent> Events { get; } = [];

    /// <summary>When set, the head reported to the exporter. Models a GC that moved on after the export started.</summary>
    public long? HeadOverride { get; set; }

    /// <summary>When set, the viewer may read only through this seq, as a member who left would.</summary>
    public long? GrantThroughSeq { get; set; }

    /// <summary>When true, every read is refused as the real reader refuses a viewer without access.</summary>
    public bool Denied { get; set; }

    public async IAsyncEnumerable<GcEvent> ReadAsync(
        Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (Denied)
        {
            throw new GcAccessDeniedException(gcId);
        }

        var through = GrantThroughSeq ?? long.MaxValue;
        foreach (var ev in Events.Where(e => e.Seq > afterSeq && e.Seq <= through).Take(limit))
        {
            yield return ev;
        }
    }

    public Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct) =>
        Task.FromResult(HeadOverride ?? (Events.Count == 0 ? 0 : Events.Max(e => e.Seq)));
}

/// <summary>An in-memory version source that pages by seq.</summary>
internal sealed class FakeVersionSource : IGcMessageVersionSource
{
    public List<GcMessageVersionRecord> Rows { get; } = [];

    public async IAsyncEnumerable<GcMessageVersionRecord> ReadAsync(
        Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var row in Rows.Where(r => r.Seq > afterSeq).Take(limit))
        {
            yield return row;
        }
    }
}

/// <summary>
/// An in-memory export store. A file is stored when its stream is disposed, as a real store would; a test can replace or remove
/// a stored file to simulate damage after the export.
/// </summary>
internal sealed class InMemoryExportFileStore : IExportFileStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public Task<Stream> CreateAsync(string path, CancellationToken ct)
    {
        if (_files.ContainsKey(path))
        {
            throw new IOException("The file already exists: " + path);
        }

        return Task.FromResult<Stream>(new CommitOnClose(bytes => _files[path] = bytes));
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        if (!_files.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException(path);
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public byte[] Get(string path) => _files[path];

    public string Text(string path) => Encoding.UTF8.GetString(_files[path]);

    public string[] Lines(string path) => Text(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public void Replace(string path, byte[] bytes) => _files[path] = bytes;

    public void Remove(string path) => _files.Remove(path);

    /// <summary>A memory stream that stores its bytes when it is closed, as a file store does.</summary>
    private sealed class CommitOnClose(Action<byte[]> commit) : MemoryStream
    {
        private bool _committed;

        private void Commit()
        {
            if (!_committed)
            {
                _committed = true;
                commit(ToArray());
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Commit();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Commit();
            await base.DisposeAsync();
        }
    }
}
