namespace Bazaar.Testing.Replay;

/// <summary>What differed at the first mismatch.</summary>
public enum ReplayMismatchReason
{
    /// <summary>The record kind differs.</summary>
    Kind,

    /// <summary>The sequence number differs, so the records are in a different order.</summary>
    Order,

    /// <summary>A structural key differs.</summary>
    Key,

    /// <summary>The expected transcript has a record the actual one lacks.</summary>
    MissingRecord,

    /// <summary>The actual transcript has a record the expected one does not.</summary>
    ExtraRecord,
}

/// <summary>The first difference between two transcripts. <see cref="Index"/> is the 0-based position in the record list.</summary>
public sealed record ReplayMismatch(int Index, ReplayMismatchReason Reason, string Detail);

/// <summary>The outcome of a comparison. <see cref="Mismatch"/> is null when the transcripts match.</summary>
public sealed record ReplayComparison(ReplayMismatch? Mismatch)
{
    public bool Matches => Mismatch is null;
}

/// <summary>
/// Compares two transcripts structurally: record count, record kind, sequence order, and the structural keys each
/// kind names. Ids, timestamps and text are ignored, so a re-recording with new ids and wording still matches.
/// Stops at the first difference.
/// </summary>
public static class ReplayComparator
{
    /// <summary>Compares <paramref name="actual"/> against <paramref name="expected"/>.</summary>
    public static ReplayComparison Compare(ReplayFixture expected, ReplayFixture actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var shared = Math.Min(expected.Records.Count, actual.Records.Count);
        for (var i = 0; i < shared; i++)
        {
            var mismatch = CompareRecord(i, expected.Records[i], actual.Records[i]);
            if (mismatch is not null)
            {
                return new ReplayComparison(mismatch);
            }
        }

        if (expected.Records.Count > actual.Records.Count)
        {
            var missing = expected.Records[shared];
            return new ReplayComparison(new ReplayMismatch(shared, ReplayMismatchReason.MissingRecord,
                $"expected {missing.Kind.WireName()} (seq {missing.Seq}) at index {shared}, but the actual transcript ends"));
        }

        if (actual.Records.Count > expected.Records.Count)
        {
            var extra = actual.Records[shared];
            return new ReplayComparison(new ReplayMismatch(shared, ReplayMismatchReason.ExtraRecord,
                $"actual has an extra {extra.Kind.WireName()} (seq {extra.Seq}) at index {shared}"));
        }

        return new ReplayComparison(null);
    }

    private static ReplayMismatch? CompareRecord(int index, ReplayRecord expected, ReplayRecord actual)
    {
        if (expected.Kind != actual.Kind)
        {
            return new ReplayMismatch(index, ReplayMismatchReason.Kind,
                $"index {index}: expected {expected.Kind.WireName()}, actual {actual.Kind.WireName()}");
        }

        if (expected.Seq != actual.Seq)
        {
            return new ReplayMismatch(index, ReplayMismatchReason.Order,
                $"index {index}: expected seq {expected.Seq}, actual seq {actual.Seq}");
        }

        foreach (var key in expected.Kind.StructuralKeys())
        {
            expected.Keys.TryGetValue(key, out var expectedValue);
            actual.Keys.TryGetValue(key, out var actualValue);
            if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                return new ReplayMismatch(index, ReplayMismatchReason.Key,
                    $"index {index}: key '{key}' expected '{expectedValue}', actual '{actualValue}'");
            }
        }

        return null;
    }
}
