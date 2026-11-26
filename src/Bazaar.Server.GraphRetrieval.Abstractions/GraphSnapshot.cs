namespace Bazaar.Server.GraphRetrieval;

/// <summary>
/// An immutable compressed-sparse-row snapshot of the node graph (doc 64 section 5.3). Nodes are dense ints
/// <c>0..NodeCount-1</c> mapped from document ids; edges are typed and weighted. A reverse CSR is built once so
/// incoming edges are as cheap as outgoing ones. Nothing here is mutated after construction, so a snapshot is
/// safe to share between concurrent queries.
/// </summary>
public sealed class GraphSnapshot
{
    private readonly string[] _docIds;
    private readonly int[] _outRow;
    private readonly int[] _outCol;
    private readonly byte[] _kind;
    private readonly float[] _weight;
    private readonly int[] _inRow;
    private readonly int[] _inSource;
    private readonly int[] _inEdge;
    private readonly Dictionary<string, int> _index;

    /// <summary>Creates a snapshot from CSR arrays. The arrays are copied.</summary>
    /// <param name="docIds">Document id per node.</param>
    /// <param name="rowOffsets">Length <c>NodeCount + 1</c>, non-decreasing, first 0, last the edge count.</param>
    /// <param name="colIndex">Target node per edge.</param>
    /// <param name="edgeKinds">Kind per edge (see <see cref="GraphEdgeKind"/>).</param>
    /// <param name="weights">Finite, non-negative weight per edge. A weight of 0 is never followed.</param>
    /// <param name="watermark">The index watermark this snapshot was built at.</param>
    public GraphSnapshot(
        IReadOnlyList<string> docIds,
        IReadOnlyList<int> rowOffsets,
        IReadOnlyList<int> colIndex,
        IReadOnlyList<byte> edgeKinds,
        IReadOnlyList<float> weights,
        long watermark = 0)
    {
        ArgumentNullException.ThrowIfNull(docIds);
        ArgumentNullException.ThrowIfNull(rowOffsets);
        ArgumentNullException.ThrowIfNull(colIndex);
        ArgumentNullException.ThrowIfNull(edgeKinds);
        ArgumentNullException.ThrowIfNull(weights);

        int n = docIds.Count;
        int e = colIndex.Count;
        if (rowOffsets.Count != n + 1)
        {
            throw new ArgumentException("rowOffsets must have NodeCount + 1 entries.", nameof(rowOffsets));
        }

        if (edgeKinds.Count != e || weights.Count != e)
        {
            throw new ArgumentException("colIndex, edgeKinds and weights must have the same length.");
        }

        _docIds = [.. docIds];
        _outRow = [.. rowOffsets];
        _outCol = [.. colIndex];
        _kind = [.. edgeKinds];
        _weight = [.. weights];
        Watermark = watermark;

        if (_outRow[0] != 0 || _outRow[n] != e)
        {
            throw new ArgumentException("rowOffsets must start at 0 and end at the edge count.", nameof(rowOffsets));
        }

        for (int i = 0; i < n; i++)
        {
            if (_outRow[i + 1] < _outRow[i])
            {
                throw new ArgumentException("rowOffsets must be non-decreasing.", nameof(rowOffsets));
            }
        }

        for (int i = 0; i < e; i++)
        {
            if ((uint)_outCol[i] >= (uint)n)
            {
                throw new ArgumentException("colIndex holds a node outside the graph.", nameof(colIndex));
            }

            if (_kind[i] >= 32)
            {
                throw new ArgumentException("Edge kinds must be below 32.", nameof(edgeKinds));
            }

            if (!float.IsFinite(_weight[i]) || _weight[i] < 0)
            {
                throw new ArgumentException("Edge weights must be finite and non-negative.", nameof(weights));
            }
        }

        _index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (!_index.TryAdd(_docIds[i], i))
            {
                throw new ArgumentException($"Duplicate document id '{_docIds[i]}'.", nameof(docIds));
            }
        }

        // Reverse CSR by counting sort. Sources are visited in ascending order, so each incoming list is ordered by source.
        _inRow = new int[n + 1];
        for (int i = 0; i < e; i++)
        {
            _inRow[_outCol[i] + 1]++;
        }

        for (int i = 0; i < n; i++)
        {
            _inRow[i + 1] += _inRow[i];
        }

        _inSource = new int[e];
        _inEdge = new int[e];
        var cursor = new int[n];
        for (int u = 0; u < n; u++)
        {
            for (int ei = _outRow[u]; ei < _outRow[u + 1]; ei++)
            {
                int v = _outCol[ei];
                int slot = _inRow[v] + cursor[v]++;
                _inSource[slot] = u;
                _inEdge[slot] = ei;
            }
        }
    }

    public int NodeCount => _docIds.Length;

    public int EdgeCount => _outCol.Length;

    /// <summary>The index watermark the snapshot was built at; echoed in every <see cref="KernelResult{T}"/>.</summary>
    public long Watermark { get; }

    public string DocId(int node) => _docIds[node];

    public bool TryGetNode(string docId, out int node) => _index.TryGetValue(docId, out node);

    public ReadOnlyMemory<int> OutRowOffsets => _outRow;

    public ReadOnlyMemory<int> OutTargets => _outCol;

    public ReadOnlyMemory<byte> EdgeKinds => _kind;

    public ReadOnlyMemory<float> EdgeWeights => _weight;

    public ReadOnlyMemory<int> InRowOffsets => _inRow;

    /// <summary>Source node per incoming edge slot.</summary>
    public ReadOnlyMemory<int> InSources => _inSource;

    /// <summary>Forward edge index per incoming edge slot, for looking up its kind and weight.</summary>
    public ReadOnlyMemory<int> InEdgeIndex => _inEdge;
}
