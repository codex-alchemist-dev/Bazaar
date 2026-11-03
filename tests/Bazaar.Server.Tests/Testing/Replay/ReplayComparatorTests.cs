using Bazaar.Testing.Replay;

namespace Bazaar.Server.Tests.Testing.Replay;

public class ReplayComparatorTests
{
    [Fact]
    public void Matching_pair_passes_despite_different_ids_timestamps_and_text()
    {
        var expected = ReplayFixtureFiles.Load("gc-thread-expected.json");
        var actual = ReplayFixtureFiles.Load("gc-thread-actual.json");

        var result = ReplayComparator.Compare(expected, actual);

        Assert.True(result.Matches);
        Assert.Null(result.Mismatch);
    }

    [Fact]
    public void Reordered_pair_fails_at_the_first_index_whose_kind_differs()
    {
        var expected = ReplayFixtureFiles.Load("gc-thread-expected.json");
        // Index 1 is a wake and index 2 a model request; swapping them puts a model request where a wake belongs.
        var actual = Swap(expected, 1, 2);

        var mismatch = ReplayComparator.Compare(expected, actual).Mismatch;

        Assert.NotNull(mismatch);
        Assert.Equal(1, mismatch.Index);
        Assert.Equal(ReplayMismatchReason.Kind, mismatch.Reason);
    }

    [Fact]
    public void Reordered_records_of_the_same_kind_fail_on_sequence_order()
    {
        var expected = ReplayFixtureFiles.Load("gc-thread-expected.json");
        // Both ends are gc_message records; the kinds match, so only the sequence number shows the reorder.
        var actual = Swap(expected, 0, 5);

        var mismatch = ReplayComparator.Compare(expected, actual).Mismatch;

        Assert.NotNull(mismatch);
        Assert.Equal(0, mismatch.Index);
        Assert.Equal(ReplayMismatchReason.Order, mismatch.Reason);
    }

    [Fact]
    public void Extra_record_fails_at_the_index_after_the_shared_prefix()
    {
        var expected = ReplayFixtureFiles.Load("gc-thread-expected.json");
        var extra = new ReplayRecord(7, ReplayKind.ToolCall, new Dictionary<string, string> { ["tool"] = "gc.read" });
        var actual = expected with { Records = [.. expected.Records, extra] };

        var mismatch = ReplayComparator.Compare(expected, actual).Mismatch;

        Assert.NotNull(mismatch);
        Assert.Equal(expected.Records.Count, mismatch.Index);
        Assert.Equal(ReplayMismatchReason.ExtraRecord, mismatch.Reason);
    }

    [Fact]
    public void Missing_record_fails_at_the_index_where_the_actual_transcript_ends()
    {
        var expected = ReplayFixtureFiles.Load("gc-thread-expected.json");
        var actual = expected with { Records = expected.Records.Take(expected.Records.Count - 1).ToList() };

        var mismatch = ReplayComparator.Compare(expected, actual).Mismatch;

        Assert.NotNull(mismatch);
        Assert.Equal(expected.Records.Count - 1, mismatch.Index);
        Assert.Equal(ReplayMismatchReason.MissingRecord, mismatch.Reason);
    }

    [Fact]
    public void Changed_structural_key_fails_and_names_the_key()
    {
        var expected = ReplayFixtureFiles.Load("gc-thread-expected.json");
        var list = expected.Records.ToList();
        list[5] = list[5] with { Keys = new Dictionary<string, string> { ["speaker"] = "agent-gamma" } };
        var actual = expected with { Records = list };

        var mismatch = ReplayComparator.Compare(expected, actual).Mismatch;

        Assert.NotNull(mismatch);
        Assert.Equal(5, mismatch.Index);
        Assert.Equal(ReplayMismatchReason.Key, mismatch.Reason);
        Assert.Contains("speaker", mismatch.Detail);
    }

    private static ReplayFixture Swap(ReplayFixture fixture, int i, int j)
    {
        var list = fixture.Records.ToList();
        (list[i], list[j]) = (list[j], list[i]);
        return fixture with { Records = list };
    }
}
