using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Tests.Wire;

public sealed class WireOptionsTests
{
    private static bool Valid(Action<WireOptions> change)
    {
        var o = new WireOptions();
        change(o);
        return new WireOptionsValidator().Validate(null, o).Succeeded;
    }

    [Fact]
    public void Defaults_are_valid() => Assert.True(Valid(_ => { }));

    [Fact]
    public void Tightening_a_floor_is_allowed() =>
        Assert.True(Valid(o =>
        {
            o.MaxFrameKib = 64;
            o.MaxSocketsPerDevice = 2;
            o.HeartbeatTimeoutS = 30;
            o.HeartbeatIntervalS = 10;
        }));

    [Theory]
    [InlineData(nameof(WireOptions.MaxFrameKib), 512)]
    [InlineData(nameof(WireOptions.MaxSocketsPerDevice), 6)]
    [InlineData(nameof(WireOptions.InflightRequestsMax), 64)]
    [InlineData(nameof(WireOptions.OutboundQueueMax), 2000)]
    [InlineData(nameof(WireOptions.HeartbeatTimeoutS), 120)]
    [InlineData(nameof(WireOptions.HelloTimeoutS), 10)]
    [InlineData(nameof(WireOptions.RequestIdRetentionH), 12)]
    [InlineData(nameof(WireOptions.MaxFrameKib), 0)]
    public void Loosening_a_floor_is_refused(string property, int value) =>
        Assert.False(Valid(o => typeof(WireOptions).GetProperty(property)!.SetValue(o, value)));

    [Fact]
    public void Heartbeat_interval_must_be_below_the_timeout() =>
        Assert.False(Valid(o =>
        {
            o.HeartbeatIntervalS = 20;
            o.HeartbeatTimeoutS = 20;
        }));

    [Fact]
    public void Path_must_be_rooted() => Assert.False(Valid(o => o.Path = "ws"));
}
