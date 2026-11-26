using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Embeddings;

/// <summary>
/// Deterministic hash-based vectors for tests and development (doc 08, "WP6 detail"). The same text always gives the same
/// unit-length vector; different texts give unrelated vectors. It has no semantic meaning.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public const string DefaultModelId = "fake-hash";

    public FakeEmbeddingProvider(int dimensions = 8, string modelId = DefaultModelId)
    {
        Descriptor = new EmbeddingProviderDescriptor(EmbeddingProviderKind.Fake, modelId, dimensions);
    }

    public EmbeddingProviderDescriptor Descriptor { get; }

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            vectors[i] = Embed(texts[i] ?? throw new ArgumentException("Texts must not contain null.", nameof(texts)));
        }

        return Task.FromResult(new EmbeddingResult(vectors));
    }

    private float[] Embed(string text)
    {
        var dims = Descriptor.Dimensions;
        var vector = new float[dims];
        var input = Encoding.UTF8.GetBytes(text);
        var seed = new byte[input.Length + sizeof(int)];
        input.CopyTo(seed, sizeof(int));
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        var norm = 0.0;
        for (var i = 0; i < dims; i += 8)
        {
            // One SHA-256 per eight values: the counter prefix keeps the blocks independent.
            BinaryPrimitives.WriteInt32LittleEndian(seed, i / 8);
            SHA256.HashData(seed, digest);
            for (var j = 0; j < 8 && i + j < dims; j++)
            {
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(digest[(j * 4)..]);
                var value = (float)((raw / (double)uint.MaxValue * 2.0) - 1.0);
                vector[i + j] = value;
                norm += (double)value * value;
            }
        }

        if (norm > 0)
        {
            var scale = (float)(1.0 / Math.Sqrt(norm));
            for (var i = 0; i < dims; i++)
            {
                vector[i] *= scale;
            }
        }

        return vector;
    }
}
