namespace Bazaar.Server.CodingReview;

/// <summary>
/// Picks the reviewer from the author's visible pool (doc 17, Review flow). A model from a different family is preferred. When the
/// pool has none, the chooser falls back to the same family and marks <see cref="ReviewerModelChoice.SameFamily"/>. The choice is
/// deterministic: candidates are ordered by family, then model ID, both ordinal. An empty pool gives null, which means no review.
/// </summary>
public static class ReviewerModelChooser
{
    /// <summary>Chooses the reviewer for <paramref name="pool"/>, or returns null when no usable candidate exists.</summary>
    public static ReviewerModelChoice? Choose(ReviewerPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var candidates = pool.Candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.ModelId) && !string.IsNullOrWhiteSpace(c.Family))
            .OrderBy(c => c.Family, StringComparer.Ordinal)
            .ThenBy(c => c.ModelId, StringComparer.Ordinal)
            .ToList();

        var otherFamily = candidates.FirstOrDefault(c => !string.Equals(c.Family, pool.AuthorFamily, StringComparison.OrdinalIgnoreCase));
        if (otherFamily is not null)
        {
            return new ReviewerModelChoice(otherFamily.ModelId, otherFamily.Family, SameFamily: false);
        }

        // Same family only. Prefer a model other than the author's own exact model; if the pool holds only that model, use it and
        // keep the flag, so the review is still recorded as same-family.
        var sameFamily = candidates.FirstOrDefault(c => !string.Equals(c.ModelId, pool.AuthorModelId, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault();
        return sameFamily is null ? null : new ReviewerModelChoice(sameFamily.ModelId, sameFamily.Family, SameFamily: true);
    }
}
