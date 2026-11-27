using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Tests.Journal;

public sealed class JournalSpillBoundTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kodo-bound-" + Guid.NewGuid().ToString("N"));

    public JournalSpillBoundTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private string SpillPath => Path.Combine(_directory, "spill.log");

    [Fact]
    public void Nothing_is_dropped_while_the_file_fits()
    {
        Assert.Equal(0, JournalSpillBound.RecordsToDrop([100, 100, 100], maxBytes: 1000));
    }

    [Fact]
    public void The_oldest_go_first_until_the_target_share_fits_and_the_newest_stays()
    {
        int[] lengths = [.. Enumerable.Repeat(100, 20)];

        int dropped = JournalSpillBound.RecordsToDrop(lengths, maxBytes: 1000);

        Assert.InRange(dropped, 1, 19);
        long kept = lengths.Skip(dropped).Sum() + JournalSpillBound.AnchorReserve;
        Assert.True(kept <= 800);
    }

    [Fact]
    public void One_record_over_the_bound_still_keeps_the_newest()
    {
        Assert.Equal(2, JournalSpillBound.RecordsToDrop([5000, 5000, 5000], maxBytes: 1000));
        Assert.Equal(0, JournalSpillBound.RecordsToDrop([5000], maxBytes: 1000));
    }

    [Fact]
    public void The_gap_names_the_dropped_range_and_nothing_means_no_gap()
    {
        Assert.Null(JournalSpillBound.GapOf(0, 0));
        JournalGap gap = JournalSpillBound.GapOf(10, 5)!;
        Assert.Equal(10, gap.FirstSequence);
        Assert.Equal(14, gap.LastSequence);
        Assert.Equal(5, gap.Count);
    }

    [Fact]
    public void Megabytes_convert_and_zero_is_refused()
    {
        Assert.Equal(512L * 1024 * 1024, JournalSpillBound.BytesFromMegabytes(512));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalSpillBound.BytesFromMegabytes(0));
    }

    [Fact]
    public void Sealed_names_sort_by_time_and_are_recognised()
    {
        string early = JournalSpillBound.SealedName("/d/spill", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        string late = JournalSpillBound.SealedName("/d/spill", new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(early, late) < 0);
        Assert.True(JournalSpillBound.IsSealedName("/d/spill", early));
        Assert.False(JournalSpillBound.IsSealedName("/d/spill", "/d/spill"));
    }

    [Fact]
    public void The_anchor_line_round_trips_and_malformed_lines_are_refused()
    {
        JournalAnchor anchor = new(42, new byte[32], 7);
        byte[] line = anchor.FormatLine();

        JournalAnchor parsed = JournalAnchor.Parse(line.AsSpan(0, line.Length - 1));

        Assert.Equal(42, parsed.NextSequence);
        Assert.Equal(7, parsed.DroppedRecords);
        Assert.Equal(anchor.PreviousHash, parsed.PreviousHash);
        Assert.Throws<InvalidDataException>(() => JournalAnchor.Parse("@anchor 1 zz 0"u8));
        Assert.Throws<InvalidDataException>(() => JournalAnchor.Parse("@anchor -1 00 0"u8));
    }

    [Fact]
    public void A_bounded_spill_keeps_the_newest_reports_the_gap_and_still_verifies()
    {
        long bound = 6000;
        List<JournalRecord> written = [];
        JournalGap? gap = null;
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath, bound))
        {
            foreach (JournalRecord sample in JournalSamples.Chain(seed: 11, count: 60))
            {
                written.Add(writer.Append(sample.At, sample.Kind, sample.Payload));
                gap = writer.TakeGap() ?? gap;
            }
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);

        Assert.NotNull(gap);
        Assert.NotNull(read.Anchor);
        Assert.True(new FileInfo(SpillPath).Length <= bound);
        Assert.True(read.Verify().IsValid);
        Assert.Equal(written[^1].Hash, read.Records[^1].Hash);
        Assert.Equal(read.Anchor.NextSequence, read.Records[0].Sequence);
        Assert.Equal(written[(int)read.Anchor.NextSequence - 1].Hash, read.Anchor.PreviousHash);
        Assert.Equal(read.Anchor.NextSequence, read.Anchor.DroppedRecords);
        Assert.False(File.Exists(SpillPath + ".compact"));
    }

    [Fact]
    public void A_gap_is_never_filled_the_dropped_sequences_are_absent()
    {
        JournalGap? gap = null;
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath, 4000))
        {
            foreach (JournalRecord sample in JournalSamples.Chain(seed: 12, count: 40))
            {
                writer.Append(sample.At, sample.Kind, sample.Payload);
                gap ??= writer.TakeGap();
            }
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);

        Assert.NotNull(gap);
        Assert.Equal(0, gap.FirstSequence);
        Assert.DoesNotContain(read.Records, record => record.Sequence <= gap.LastSequence);
        Assert.Equal(40, read.Records[^1].Sequence + 1);
    }

    [Fact]
    public void A_compacted_spill_reopens_and_appends_continue_the_chain()
    {
        List<JournalRecord> samples = JournalSamples.Chain(seed: 13, count: 50);
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath, 4000))
        {
            foreach (JournalRecord sample in samples.Take(40))
            {
                writer.Append(sample.At, sample.Kind, sample.Payload);
            }
        }

        using (JournalSpillWriter reopened = JournalSpillWriter.Open(SpillPath, 4000))
        {
            Assert.Equal(40, reopened.NextSequence);
            foreach (JournalRecord sample in samples.Skip(40))
            {
                reopened.Append(sample.At, sample.Kind, sample.Payload);
            }
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);
        Assert.True(read.Verify().IsValid);
        Assert.Equal(49, read.Records[^1].Sequence);
    }

    [Fact]
    public void A_tampered_first_kept_record_or_anchor_does_not_verify()
    {
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath, 4000))
        {
            foreach (JournalRecord sample in JournalSamples.Chain(seed: 14, count: 40))
            {
                writer.Append(sample.At, sample.Kind, sample.Payload);
            }
        }

        JournalSpillRead read = JournalSpill.Read(SpillPath);
        JournalAnchor wrong = read.Anchor! with { PreviousHash = JournalSamples.FlipByte(read.Anchor!.PreviousHash, 0) };
        JournalAnchor shifted = read.Anchor with { NextSequence = read.Anchor.NextSequence + 1 };

        Assert.Equal(0, JournalChain.Verify(read.Records, wrong).FirstBrokenIndex);
        Assert.Equal(0, JournalChain.Verify(read.Records, shifted).FirstBrokenIndex);
        Assert.False(JournalChain.Verify(read.Records).IsValid);
    }

    [Fact]
    public void A_spill_with_a_corrupt_anchor_is_refused_on_open()
    {
        File.WriteAllText(SpillPath, "@anchor 5 nothex 0\n");

        Assert.Throws<InvalidDataException>(() => JournalSpillWriter.Open(SpillPath));
    }

    [Fact]
    public void A_file_already_over_a_lowered_bound_is_cut_down_by_the_next_append()
    {
        List<JournalRecord> samples = JournalSamples.Chain(seed: 15, count: 40);
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
        {
            foreach (JournalRecord sample in samples.Take(30))
            {
                writer.Append(sample.At, sample.Kind, sample.Payload);
            }
        }

        using (JournalSpillWriter bounded = JournalSpillWriter.Open(SpillPath, 3000))
        {
            bounded.Append(samples[30].At, samples[30].Kind, samples[30].Payload);
            Assert.NotNull(bounded.TakeGap());
            Assert.Null(bounded.TakeGap());
        }

        Assert.True(new FileInfo(SpillPath).Length <= 3000);
        Assert.True(JournalSpill.Read(SpillPath).Verify().IsValid);
    }

    [Fact]
    public void Sealing_moves_the_spill_aside_and_the_new_file_continues_the_chain()
    {
        List<JournalRecord> samples = JournalSamples.Chain(seed: 16, count: 10);
        string sealedPath = JournalSpillBound.SealedName(SpillPath, new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
        {
            foreach (JournalRecord sample in samples.Take(6))
            {
                writer.Append(sample.At, sample.Kind, sample.Payload);
            }

            Assert.True(writer.Seal(sealedPath));
            Assert.False(writer.Seal(sealedPath + "-again"));
            foreach (JournalRecord sample in samples.Skip(6))
            {
                writer.Append(sample.At, sample.Kind, sample.Payload);
            }
        }

        JournalSpillRead archive = JournalSpill.Read(sealedPath);
        JournalSpillRead active = JournalSpill.Read(SpillPath);
        Assert.Equal(6, archive.Records.Count);
        Assert.True(archive.Verify().IsValid);
        Assert.Equal(4, active.Records.Count);
        Assert.Equal(6, active.Anchor!.NextSequence);
        Assert.Equal(0, active.Anchor.DroppedRecords);
        Assert.True(active.Verify().IsValid);
        Assert.Equal(archive.Records[^1].Hash, active.Records[0].PreviousHash);
    }

    [Fact]
    public void Sealing_onto_an_existing_name_fails_and_keeps_the_old_file_active()
    {
        string sealedPath = SpillPath + ".sealed-x";
        File.WriteAllText(sealedPath, "occupied");
        JournalRecord sample = JournalSamples.Chain(17, 1)[0];
        using (JournalSpillWriter writer = JournalSpillWriter.Open(SpillPath))
        {
            writer.Append(sample.At, sample.Kind, sample.Payload);

            Assert.ThrowsAny<IOException>(() => writer.Seal(sealedPath));
            writer.Append(sample.At, sample.Kind, sample.Payload);
        }

        Assert.Equal(2, JournalSpill.Read(SpillPath).Records.Count);
    }
}
