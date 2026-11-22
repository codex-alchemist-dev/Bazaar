namespace Bazaar.Client.Rubedo.Playground.Tests;

public class PlaygroundViewModelTests
{
    private static PlaygroundViewModel Phone()
    {
        var vm = new PlaygroundViewModel { Source = "deviceClass == \"phone\"" };
        return vm;
    }

    [Fact]
    public void Default_source_evaluates_against_the_default_context()
    {
        var vm = new PlaygroundViewModel();

        Assert.Equal("false", vm.ResultText);
        Assert.Empty(vm.Diagnostics);
    }

    [Fact]
    public void Changing_the_source_recomputes_the_result()
    {
        var vm = new PlaygroundViewModel { Source = "1 + 2" };

        Assert.Equal("3", vm.ResultText);
    }

    [Fact]
    public void Text_results_show_their_quotes()
    {
        var vm = new PlaygroundViewModel { Source = "deviceClass" };

        Assert.Equal("\"desktop\"", vm.ResultText);
    }

    [Fact]
    public void Invalid_source_reports_a_diagnostic_with_code_and_offsets()
    {
        var vm = new PlaygroundViewModel { Source = "1 +" };

        var line = Assert.Single(vm.Diagnostics);
        Assert.False(string.IsNullOrEmpty(line.Code));
        Assert.True(line.End >= line.Start);
        Assert.Contains(line.Code, line.Text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.ResultText);
        Assert.True(vm.HasDiagnostics);
    }

    [Fact]
    public void Unknown_identifier_is_a_diagnostic()
    {
        var vm = new PlaygroundViewModel { Source = "nope == 1" };

        Assert.NotEmpty(vm.Diagnostics);
    }

    [Fact]
    public void Runtime_error_is_a_diagnostic()
    {
        var vm = new PlaygroundViewModel { Source = "1 / 0 > 0" };

        Assert.True(vm.HasDiagnostics);
        Assert.Equal(string.Empty, vm.ResultText);
    }

    [Fact]
    public void Device_class_control_changes_the_result()
    {
        var vm = Phone();
        Assert.Equal("false", vm.ResultText);

        vm.DeviceClass = "phone";

        Assert.Equal("true", vm.ResultText);
    }

    // Each of the other eight controls feeds the context: a source that reads only that identifier changes with it.
    [Theory]
    [InlineData("display", "cover")]
    [InlineData("posture", "folded")]
    [InlineData("colorScheme", "dark")]
    public void Text_controls_change_the_result(string identifier, string value)
    {
        var vm = new PlaygroundViewModel { Source = identifier + " == \"" + value + "\"" };
        Assert.Equal("false", vm.ResultText);

        switch (identifier)
        {
            case "display": vm.Display = value; break;
            case "posture": vm.Posture = value; break;
            default: vm.ColorScheme = value; break;
        }

        Assert.Equal("true", vm.ResultText);
    }

    [Fact]
    public void Number_controls_change_the_result()
    {
        var vm = new PlaygroundViewModel { Source = "widthDp" };
        vm.WidthDp = 360;
        Assert.Equal("360", vm.ResultText);

        vm.Source = "heightDp";
        vm.HeightDp = 640;
        Assert.Equal("640", vm.ResultText);

        vm.Source = "textScale";
        vm.TextScale = 1.5m;
        Assert.Equal("1.5", vm.ResultText);
    }

    [Fact]
    public void Bool_controls_change_the_result()
    {
        var vm = new PlaygroundViewModel { Source = "reduceMotion" };
        Assert.Equal("false", vm.ResultText);
        vm.ReduceMotion = true;
        Assert.Equal("true", vm.ResultText);

        vm.Source = "isOwner";
        Assert.Equal("true", vm.ResultText);
        vm.IsOwner = false;
        Assert.Equal("false", vm.ResultText);
    }

    [Fact]
    public void Format_rewrites_the_source_canonically()
    {
        var vm = new PlaygroundViewModel { Source = "1+2" };

        Assert.True(vm.Format());

        Assert.Equal("1 + 2", vm.Source);
        Assert.Equal("3", vm.ResultText);
        Assert.Equal(string.Empty, vm.FormatMessage);
    }

    [Fact]
    public void Format_of_invalid_source_leaves_it_and_says_so()
    {
        var vm = new PlaygroundViewModel { Source = "1+" };

        Assert.False(vm.Format());

        Assert.Equal("1+", vm.Source);
        Assert.Equal(vm.Text(PlaygroundText.FormatFailed), vm.FormatMessage);
    }

    [Fact]
    public void View_mode_diagnoses_a_view_document()
    {
        var vm = new PlaygroundViewModel { Mode = PlaygroundMode.View, Source = "{ not json" };

        Assert.True(vm.HasDiagnostics);
        Assert.False(vm.CanFormat);
        Assert.False(vm.Format());
        Assert.Equal(vm.Text(PlaygroundText.FormatUnavailable), vm.FormatMessage);
    }

    [Fact]
    public void Property_changes_are_raised_for_the_result()
    {
        var vm = new PlaygroundViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.DeviceClass = "phone";

        Assert.Contains(nameof(PlaygroundViewModel.ResultText), raised);
    }
}
