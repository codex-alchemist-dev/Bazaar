using Bazaar.Client.LocalInference.Guards;

namespace Bazaar.Client.LocalInference.Tests.Guards;

public class OutputGuardTests
{
    private const string _input = "Moved 15 files for Priya to https://example.com/docs/plan, see \"Blue Harbour\".";

    [Fact]
    public void Dropped_number_fails_the_guard()
    {
        var report = OutputGuard.Check("Moved 15 files", "Moved files");

        Assert.False(report.Passed);
        Assert.Equal(["15"], report.DroppedNumbers);
    }

    [Fact]
    public void Number_must_match_as_a_whole_token()
    {
        var report = OutputGuard.Check("Moved 15 files", "Moved 150 files");

        Assert.Equal(["15"], report.DroppedNumbers);
    }

    [Fact]
    public void Dropped_name_fails_the_guard()
    {
        var report = OutputGuard.Check("Ask Priya about the deploy", "Ask about the deploy");

        Assert.False(report.Passed);
        Assert.Equal(["Priya"], report.DroppedNames);
    }

    [Fact]
    public void Dropped_quoted_span_fails_the_guard()
    {
        var report = OutputGuard.Check("Call \"Blue Harbour\" tomorrow", "Call tomorrow");

        Assert.Contains("Blue Harbour", report.DroppedNames);
    }

    [Fact]
    public void Name_is_matched_as_a_whole_word()
    {
        var report = OutputGuard.Check("Ask Priya", "Ask Priyanka");

        Assert.Equal(["Priya"], report.DroppedNames);
    }

    [Fact]
    public void Dropped_URL_fails_the_guard()
    {
        var report = OutputGuard.Check("See https://example.com/docs/a for details", "See the docs for details");

        Assert.False(report.Passed);
        Assert.Equal(["https://example.com/docs/a"], report.DroppedUrls);
    }

    [Fact]
    public void Trailing_punctuation_is_not_part_of_the_URL()
    {
        var report = OutputGuard.Check("Read https://example.com/a.", "Read https://example.com/a");

        Assert.True(report.Passed);
    }

    [Fact]
    public void Intact_output_passes_the_guard()
    {
        var output = "Moved 15 files for Priya to https://example.com/docs/plan, see \"Blue Harbour\".";

        var report = OutputGuard.Check(_input, output);

        Assert.True(report.Passed);
    }

    [Fact]
    public void Rephrased_output_that_keeps_every_token_passes()
    {
        var output = "Priya moved 15 files: see \"Blue Harbour\" at https://example.com/docs/plan.";

        Assert.True(OutputGuard.Check(_input, output).Passed);
    }

    [Fact]
    public void Single_capital_letters_are_not_names()
    {
        var report = OutputGuard.Check("I moved it", "moved it");

        Assert.True(report.Passed);
    }

    [Fact]
    public void Apply_shows_the_model_output_when_the_guard_passes()
    {
        var output = "Priya moved 15 files.";

        var result = OutputGuard.Apply(LocalTaskKind.ChipSummary, "Priya moved 15 files", output);

        Assert.False(result.FellBack);
        Assert.Equal(output, result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Apply_returns_the_raw_input_when_a_guard_fails()
    {
        var input = "Priya moved 15 files";

        var result = OutputGuard.Apply(LocalTaskKind.ChipSummary, input, "Someone moved some files");

        Assert.True(result.FellBack);
        Assert.Equal(input, result.Text);
        Assert.False(result.Report.Passed);
    }

    [Fact]
    public void Apply_returns_the_raw_input_for_empty_output()
    {
        var result = OutputGuard.Apply(LocalTaskKind.VoiceCleanup, "no numbers here", "   ");

        Assert.True(result.FellBack);
        Assert.Equal("no numbers here", result.Text);
    }

    [Fact]
    public void Guard_runs_on_the_capped_text()
    {
        var input = "Moved 15 files";
        var output = new string('x', OutputCaps.CapFor(LocalTaskKind.ChipSummary)) + " 15";

        var result = OutputGuard.Apply(LocalTaskKind.ChipSummary, input, output);

        Assert.True(result.FellBack);
        Assert.True(result.Truncated);
        Assert.Equal(input, result.Text);
    }
}
