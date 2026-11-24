namespace Bazaar.Client.Windows.Tests;

public class WindowsReducedMotionTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    public void Animations_switched_off_by_a_successful_read_mean_reduced_motion(bool succeeded, int enabled, bool expected) =>
        Assert.Equal(expected, WindowsReducedMotion.Map(succeeded, enabled));

    [Fact]
    public void Off_windows_the_live_read_is_not_reduced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(WindowsReducedMotion.Read());
    }

    [Fact]
    public void The_flag_is_the_documented_spi_code() => Assert.Equal(0x1042u, WindowsReducedMotion.SpiGetClientAreaAnimation);
}
