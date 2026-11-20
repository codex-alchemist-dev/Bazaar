using Bazaar.Server.Embeddings.Indexing;

namespace Bazaar.Server.Embeddings.Tests;

internal sealed class FakeSettingSource(string value) : IEmbeddingSettingSource
{
    public string Value { get; set; } = value;

    public Task<string> GetEmbeddingProfileSettingAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
}

/// <summary>In-memory <c>embedding_profile</c>: rows are kept, so create, find and activate behave like the table.</summary>
internal sealed class FakeProfileStore : IEmbeddingProfileStore
{
    public FakeProfileStore(EmbeddingProfile? active)
    {
        if (active is not null)
        {
            Rows.Add(active);
        }
    }

    public List<EmbeddingProfile> Rows { get; } = [];

    public int Creates { get; private set; }

    public int Activations { get; private set; }

    public EmbeddingProfile? Active
    {
        get => Rows.SingleOrDefault(r => r.Active);
        set
        {
            Rows.RemoveAll(r => r.Active);
            if (value is not null)
            {
                Rows.Add(value);
            }
        }
    }

    public Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);

    public Task<EmbeddingProfile?> FindAsync(string providerId, string modelId, int dimensions, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.ProviderId == providerId && r.ModelId == modelId && r.Dimensions == dimensions));

    public Task<EmbeddingProfile> CreateAsync(string providerId, string modelId, int dimensions, CancellationToken cancellationToken)
    {
        Creates++;
        var row = new EmbeddingProfile(Guid.NewGuid(), providerId, modelId, dimensions, DateTimeOffset.UnixEpoch, false);
        Rows.Add(row);
        return Task.FromResult(row);
    }

    public Task ActivateAsync(Guid profileId, CancellationToken cancellationToken)
    {
        Activations++;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i] = Rows[i] with { Active = Rows[i].ProfileId == profileId };
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeRegistry(params (string Id, IEmbeddingProvider Provider)[] entries) : IEmbeddingProviderRegistry
{
    private readonly Dictionary<string, IEmbeddingProvider> _map = entries.ToDictionary(e => e.Id, e => e.Provider);

    public IReadOnlyCollection<string> ProviderIds => _map.Keys;

    public bool TryGet(string providerId, out IEmbeddingProvider provider) => _map.TryGetValue(providerId, out provider!);
}

/// <summary>In-memory stand-in for the two Vault tables: blocks, and embeddings keyed by (doc, ordinal).</summary>
internal sealed class InMemoryIndexStore : IEmbeddingIndexStore
{
    public List<PendingBlock> Blocks { get; } = [];

    public Dictionary<(Guid Doc, int Ordinal), byte[]> Stored { get; } = [];

    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<PendingBlock>> ListPendingAsync(Guid profileId, BlockCursor? after, int limit, CancellationToken cancellationToken)
    {
        ListCalls++;
        IReadOnlyList<PendingBlock> page = Blocks
            .OrderBy(b => b.DocId).ThenBy(b => b.Ordinal)
            .Where(b => after is null || b.DocId.CompareTo(after.DocId) > 0 || (b.DocId == after.DocId && b.Ordinal > after.Ordinal))
            .Where(b => !Stored.TryGetValue((b.DocId, b.Ordinal), out var hash) || !hash.AsSpan().SequenceEqual(b.TextHash))
            .Take(limit)
            .ToArray();
        return Task.FromResult(page);
    }

    public Task<int> UpsertAsync(Guid profileId, IReadOnlyList<EmbeddedBlock> embeddings, CancellationToken cancellationToken)
    {
        foreach (var e in embeddings)
        {
            Stored[(e.DocId, e.Ordinal)] = e.TextHash;
        }

        return Task.FromResult(embeddings.Count);
    }
}

internal sealed class CountingProvider(IEmbeddingProvider inner) : IEmbeddingProvider
{
    public List<string> Seen { get; } = [];

    public bool Fail { get; set; }

    public EmbeddingProviderDescriptor Descriptor => inner.Descriptor;

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EmbeddingException("provider down");
        }

        Seen.AddRange(texts);
        return inner.EmbedAsync(texts, cancellationToken);
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return respond(request, body);
    }
}
