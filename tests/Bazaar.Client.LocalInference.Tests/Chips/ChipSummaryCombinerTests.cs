using Bazaar.Client.LocalInference.Chips;

namespace Bazaar.Client.LocalInference.Tests.Chips;

public class ChipSummaryCombinerTests
{
    private const string _label = "Read 15 files (.cs)";
    private const string _input = "Priya read 15 files in src/Bazaar.Client.LocalInference";

    [Fact]
    public void Model_summary_is_used_when_the_guard_passes()
    {
        var chip = ChipSummaryCombiner.Combine(_label, _input, "Priya read 15 files in Bazaar.Client.LocalInference");

        Assert.True(chip.UsedModelSummary);
        Assert.Equal("Priya read 15 files in Bazaar.Client.LocalInference", chip.Text);
    }

    [Fact]
    public void Label_is_shown_when_a_number_is_dropped()
    {
        var chip = ChipSummaryCombiner.Combine(_label, _input, "Priya read some files in Bazaar.Client.LocalInference");

        Assert.False(chip.UsedModelSummary);
        Assert.Equal(_label, chip.Text);
    }

    [Fact]
    public void Label_is_shown_when_a_name_is_dropped()
    {
        var chip = ChipSummaryCombiner.Combine(_label, _input, "Someone read 15 files in Bazaar.Client.LocalInference");

        Assert.Equal(_label, chip.Text);
    }

    [Fact]
    public void Label_is_shown_when_there_is_no_model_line()
    {
        var chip = ChipSummaryCombiner.Combine(_label, _input, null);

        Assert.False(chip.UsedModelSummary);
        Assert.Equal(_label, chip.Text);
    }

    [Fact]
    public void Blank_model_output_gives_the_label()
    {
        var chip = ChipSummaryCombiner.Combine(_label, _input, "   \n  ");

        Assert.False(chip.UsedModelSummary);
        Assert.Equal(_label, chip.Text);
    }

    [Fact]
    public void Label_is_kept_in_both_cases()
    {
        var used = ChipSummaryCombiner.Combine(_label, _input, "Priya read 15 files in Bazaar.Client.LocalInference");
        var fallback = ChipSummaryCombiner.Combine(_label, _input, "Nothing useful");

        Assert.Equal(_label, used.Label);
        Assert.Equal(_label, fallback.Label);
    }

    [Fact]
    public void Only_the_first_non_empty_line_of_the_model_output_is_used()
    {
        var chip = ChipSummaryCombiner.Combine(_label, _input, "\nPriya read 15 files in Bazaar.Client.LocalInference\nSecond line");

        Assert.Equal("Priya read 15 files in Bazaar.Client.LocalInference", chip.Text);
    }
}
