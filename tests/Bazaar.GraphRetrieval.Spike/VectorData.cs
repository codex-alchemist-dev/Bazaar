using System.Numerics;
using System.Runtime.Intrinsics;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>Deterministic synthetic embeddings: random unit vectors, stored as fp16 values (held as float), with planted queries.</summary>
internal sealed class VectorData
{
    public const int RowsPerChunk = 8192;

    public VectorData(long rows, int dim, int seed = 1)
    {
        Rows = rows;
        Dim = dim;
        Seed = seed;
        int chunks = (int)((rows + RowsPerChunk - 1) / RowsPerChunk);
        Chunks = new float[chunks][];
        InverseNorm = new float[chunks][];
        Parallel.For(0, chunks, c =>
        {
            int count = (int)Math.Min(RowsPerChunk, rows - ((long)c * RowsPerChunk));
            var data = new float[count * dim];
            var inv = new float[count];
            for (int r = 0; r < count; r++)
            {
                Generate((long)c * RowsPerChunk + r, data.AsSpan(r * dim, dim));
                // The stored value is the fp16 rounding of the unit vector; the norm is that of the stored values.
                double sum = 0;
                for (int d = 0; d < dim; d++)
                {
                    sum += (double)data[(r * dim) + d] * data[(r * dim) + d];
                }

                inv[r] = (float)(1.0 / Math.Sqrt(sum));
            }

            Chunks[c] = data;
            InverseNorm[c] = inv;
        });
    }

    public long Rows { get; }

    public int Dim { get; }

    public int Seed { get; }

    public float[][] Chunks { get; }

    public float[][] InverseNorm { get; }

    public ReadOnlySpan<float> Row(long row) => Chunks[row / RowsPerChunk].AsSpan((int)(row % RowsPerChunk) * Dim, Dim);

    public float InvNorm(long row) => InverseNorm[row / RowsPerChunk][row % RowsPerChunk];

    /// <summary>Fills <paramref name="target"/> with the unit vector of <paramref name="row"/>, rounded to fp16 and back.</summary>
    private void Generate(long row, Span<float> target)
    {
        ulong state = ((ulong)row * 0x9E3779B97F4A7C15UL) ^ ((ulong)Seed * 0xD1B54A32D192ED03UL);
        double sum = 0;
        for (int d = 0; d < target.Length; d += 2)
        {
            double u1 = Next(ref state), u2 = Next(ref state);
            double radius = Math.Sqrt(-2.0 * Math.Log(u1 + 1e-12));
            target[d] = (float)(radius * Math.Cos(2 * Math.PI * u2));
            if (d + 1 < target.Length)
            {
                target[d + 1] = (float)(radius * Math.Sin(2 * Math.PI * u2));
            }
        }

        for (int d = 0; d < target.Length; d++)
        {
            sum += (double)target[d] * target[d];
        }

        float scale = (float)(1.0 / Math.Sqrt(sum));
        for (int d = 0; d < target.Length; d++)
        {
            target[d] = (float)(Half)(target[d] * scale);
        }
    }

    private static double Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return ((z >> 11) + 0.5) * (1.0 / 9007199254740992.0);
    }

    /// <summary>
    /// A query planted at cosine about 0.35 to a random stored row, so the true neighbours include one clear hit and a field of near ties
    /// (random unit vectors score about 0.19 at best among millions), which is the hard case for approximate scoring.
    /// </summary>
    public float[] PlantedQuery(int index)
    {
        long row = (long)(((ulong)index * 2654435761UL) % (ulong)Rows);
        ReadOnlySpan<float> basis = Row(row);
        var noise = new float[Dim];
        Generate(-1 - index, noise);
        const float cosine = 0.35f;
        float sine = MathF.Sqrt(1 - (cosine * cosine));
        var query = new float[Dim];
        double norm = 0;
        for (int d = 0; d < Dim; d++)
        {
            query[d] = (cosine * basis[d] * InvNorm(row)) + (sine * noise[d]);
            norm += (double)query[d] * query[d];
        }

        float inv = (float)(1.0 / Math.Sqrt(norm));
        for (int d = 0; d < Dim; d++)
        {
            query[d] *= inv;
        }

        return query;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var acc = Vector256<float>.Zero;
        int i = 0;
        for (; i + 8 <= a.Length; i += 8)
        {
            acc = Vector256.FusedMultiplyAdd(Vector256.Create(a.Slice(i, 8)), Vector256.Create(b.Slice(i, 8)), acc);
        }

        float sum = Vector256.Sum(acc);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}

/// <summary>One query's top-k: row ids and scores, best first.</summary>
internal sealed record TopK(long[] Rows, float[] Scores);

internal static class TopKTools
{
    /// <summary>Order: score descending, then row ascending (the tie key).</summary>
    public static int Compare((float Score, long Row) a, (float Score, long Row) b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Row.CompareTo(b.Row);

    public static double Recall(TopK approx, TopK exact)
    {
        var truth = new HashSet<long>(exact.Rows);
        int hit = 0;
        foreach (long row in approx.Rows)
        {
            if (truth.Contains(row))
            {
                hit++;
            }
        }

        return exact.Rows.Length == 0 ? 1.0 : hit / (double)exact.Rows.Length;
    }

    public static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
