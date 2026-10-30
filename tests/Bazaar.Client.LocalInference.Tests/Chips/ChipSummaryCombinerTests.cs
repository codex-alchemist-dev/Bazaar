using Bazaar.Client.LocalInference.Chips;

namespace Bazaar.Client.LocalInference.Tests.Chips;

public class ChipSummaryCombinerTests
{
    private const string Label = "Read 15 files (.cs)";
    private const string Input = "Priya read 15 files in src/Bazaar.Client.LocalInference";

    [Fact]
    public void Model_summary_is_used_when_the_guard_passes()
    {
        var chip = ChipSummaryCombiner.Combine(Label, Input, "Priya read 15 files in Bazaar.Client.LocalInference");

        Assert.True(chip.UsedModelSummary);
        Assert.Equal("Priya read 15 files in Bazaar.Client.LocalInference", chip.Text);
    }

    [Fact]
    public void Label_is_shown_when_a_number_is_dropped()
    {
        var chip = ChipSummaryCombiner.Combine(Label, Input, "Priya read some files in Bazaar.Client.LocalInference");

        Assert.False(chip.UsedModelSummary);
        Assert.Equal(Label, chip.Text);
    }

    [Fact]
    public void Label_is_shown_when_a_name_is_dropped()
    {
        var chip = ChipSummaryCombiner.Combine(Label, Input, "Someone read 15 files in Bazaar.Client.LocalInference");

        Assert.Equal(Label, chip.Text);
    }

    [Fact]
    public void Label_is_shown_when_there_is_no_model_line()
    {
        var chip = ChipSummaryCombiner.Combine(Label, Input, null);

        Assert.False(chip.UsedModelSummary);
        Assert.Equal(Label, chip.Text);
    }

    [Fact]
    public void Blank_model_output_gives_the_label()
    {
        var chip = ChipSummaryCombiner.Combine(Label, Input, "   \n  ");

        Assert.False(chip.UsedModelSummary);
        Assert.Equal(Label, chip.Text);
    }

    [Fact]
    public void Label_is_kept_in_both_cases()
    {
        var used = ChipSummaryCombiner.Combine(Label, Input, "Priya read 15 files in Bazaar.Client.LocalInference");
        var fallback = ChipSummaryCombiner.Combine(Label, Input, "Nothing useful");

        Assert.Equal(Label, used.Label);
        Assert.Equal(Label, fallback.Label);
    }

    [Fact]
    public void Only_the_first_non_empty_line_of_the_model_output_is_used()
    {
        var chip = ChipSummaryCombiner.Combine(Label, Input, "\nPriya read 15 files in Bazaar.Client.LocalInference\nSecond line");

        Assert.Equal("Priya read 15 files in Bazaar.Client.LocalInference", chip.Text);
    }
}
