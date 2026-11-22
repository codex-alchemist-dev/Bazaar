namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Picks the most important items when a tile holds more nodes than its draw budget allows
/// (decimation). Importance is any caller-supplied score, such as degree or cluster weight.
/// Ties go to the lower index so the choice is stable from frame to frame.
/// </summary>
public static class ImportanceRanker
{
    /// <summary>Returns the indices of the <paramref name="keep"/> highest scores, ascending by index.</summary>
    public static int[] SelectTop(ReadOnlySpan<float> importance, int keep)
    {
        if (keep <= 0 || importance.IsEmpty)
        {
            return [];
        }

        if (keep >= importance.Length)
        {
            var all = new int[importance.Length];
            for (var i = 0; i < all.Length; i++)
            {
                all[i] = i;
            }

            return all;
        }

        // Min-heap of the current best `keep`; the root is the weakest kept item.
        var heap = new PriorityQueue<int, (float Score, int NegIndex)>(keep + 1);
        for (var i = 0; i < importance.Length; i++)
        {
            var score = float.IsNaN(importance[i]) ? float.NegativeInfinity : importance[i];
            if (heap.Count < keep)
            {
                heap.Enqueue(i, (score, -i));
            }
            else if (heap.TryPeek(out _, out var weakest) && (score, -i).CompareTo(weakest) > 0)
            {
                heap.Dequeue();
                heap.Enqueue(i, (score, -i));
            }
        }

        var result = new int[heap.Count];
        var n = 0;
        while (heap.TryDequeue(out var index, out _))
        {
            result[n++] = index;
        }

        Array.Sort(result);
        return result;
    }
}
