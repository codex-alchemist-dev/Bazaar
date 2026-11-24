namespace Bazaar.Client.Linux.Tests;

public class LinuxReducedMotionTests
{
    [Theory]
    [InlineData("false\n", true)]
    [InlineData(" FALSE ", true)]
    [InlineData("true\n", false)]
    [InlineData("No such schema", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_false_enable_animations_reads_as_reduced(string? output, bool expected) =>
        Assert.Equal(expected, LinuxReducedMotion.Parse(output));

    [Fact]
    public async Task Refresh_sets_the_flag_and_raises_changed_when_it_moves()
    {
        string? output = "true\n";
        var source = new LinuxReducedMotion(() => output);
        int raised = 0;
        source.Changed += (_, _) => Interlocked.Increment(ref raised);

        await source.RefreshAsync();
        Assert.False(source.Current);
        Assert.Equal(0, raised);

        output = "false\n";
        await source.RefreshAsync();

        Assert.True(source.Current);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_missing_tool_leaves_motion_unreduced()
    {
        var source = new LinuxReducedMotion(() => null);

        await source.RefreshAsync();

        Assert.False(source.Current);
    }

    [Fact]
    public void The_settings_file_sits_in_the_state_directory()
    {
        var paths = new LinuxPaths("/state/bazaar");

        Assert.Equal(Path.Combine("/state/bazaar", "client-settings.json"), paths.SettingsFile);
    }
}
