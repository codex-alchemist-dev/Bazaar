using Bazaar.Client.LocalInference.Guards;

namespace Bazaar.Client.LocalInference.Tests.Guards;

public class OutputCapsTests
{
    [Fact]
    public void Over_cap_output_is_truncated_and_flagged()
    {
        var cap = OutputCaps.CapFor(LocalTaskKind.ChipSummary);

        var result = OutputCaps.Apply(LocalTaskKind.ChipSummary, new string('a', cap + 25));

        Assert.True(result.Truncated);
        Assert.Equal(cap, result.Text.Length);
    }

    [Fact]
    public void Output_at_the_cap_is_not_flagged()
    {
        var cap = OutputCaps.CapFor(LocalTaskKind.UiAssist);
        var text = new string('b', cap);

        var result = OutputCaps.Apply(LocalTaskKind.UiAssist, text);

        Assert.False(result.Truncated);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void Truncated_text_has_no_trailing_whitespace()
    {
        var cap = OutputCaps.CapFor(LocalTaskKind.ChipSummary);
        var text = new string('c', cap - 1) + " tail";

        var result = OutputCaps.Apply(LocalTaskKind.ChipSummary, text);

        Assert.True(result.Truncated);
        Assert.Equal(new string('c', cap - 1), result.Text);
    }

    [Fact]
    public void Every_task_kind_has_a_positive_cap()
    {
        foreach (var kind in Enum.GetValues<LocalTaskKind>())
        {
            Assert.True(OutputCaps.CapFor(kind) > 0, kind.ToString());
        }
    }
}
