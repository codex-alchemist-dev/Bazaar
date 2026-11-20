using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// The gateway seam over a <see cref="FakeProvider"/>. Pass it to <see cref="Ai.GatewayChatClient"/> to exercise scripted
/// <see cref="ModelEvent"/> steps through the <c>IChatClient</c> adapter, exactly as a real gateway call would.
/// <para>
/// <see cref="EmbedAsync"/> returns deterministic, hash-based unit vectors: the same text and dimension count always give
/// the same vector, different texts give different vectors, and the vector does not depend on the batch. No policy runs.
/// </para>
/// </summary>
public sealed class FakeModelGateway : IModelGateway
{
    /// <summary>The vector length when a request names no dimensions.</summary>
    public const int DefaultDimensions = 32;

    private readonly FakeProvider _provider;
    private readonly List<EmbeddingRequest> _embeddingRequests = [];
    private readonly Lock _gate = new();

    public FakeModelGateway(FakeProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Every embedding request this fake received, in order.</summary>
    public IReadOnlyList<EmbeddingRequest> EmbeddingRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _embeddingRequests];
            }
        }
    }

    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken) =>
        _provider.StreamAsync(request, cancellationToken);

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        cancellationToken.ThrowIfCancellationRequested();
        var dimensions = request.Dimensions ?? DefaultDimensions;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        lock (_gate)
        {
            _embeddingRequests.Add(request);
        }

        var vectors = new List<float[]>(request.Inputs.Count);
        long chars = 0;
        foreach (var input in request.Inputs)
        {
            vectors.Add(Embed(input, dimensions));
            chars += input.Length;
        }

        return Task.FromResult(new EmbeddingResult(vectors, request.Route, (chars + 3) / 4));
    }

    /// <summary>A deterministic unit vector for the text: each component comes from SHA-256 of the dimension index and the text.</summary>
    public static float[] Embed(string text, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var vector = new float[dimensions];
        var buffer = new byte[4 + bytes.Length];
        bytes.CopyTo(buffer, 4);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        double sumSquares = 0;
        for (var i = 0; i < dimensions; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), i);
            SHA256.HashData(buffer, hash);
            var component = BitConverter.ToUInt32(hash[..4]) / (double)uint.MaxValue * 2 - 1;
            vector[i] = (float)component;
            sumSquares += component * component;
        }

        var norm = (float)Math.Sqrt(sumSquares);
        if (norm > 0)
        {
            for (var i = 0; i < dimensions; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }
}
