namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head.Tests;

public class CronetFailureTests
{
    [Fact]
    public void Quic_failure_names_the_code_and_net_error()
    {
        var e = CronetFailure.ToException(new CronetFailureInfo(CronetFailureKind.Quic, 5, -100, "closed"));
        Assert.Equal("Cronet QUIC failure (connection closed, net error -100): closed", e.Message);
    }

    [Fact]
    public void Plain_cronet_failure_has_no_code()
    {
        var e = CronetFailure.ToException(new CronetFailureInfo(CronetFailureKind.Cronet, -1, 0, "engine shut down"));
        Assert.Equal("Cronet engine failure: engine shut down", e.Message);
    }

    [Theory]
    [InlineData(1, "host lookup failed")]
    [InlineData(9, "internet disconnected")]
    [InlineData(42, "error 42")]
    public void Describe_maps_known_codes(int code, string expected) => Assert.Equal(expected, CronetFailure.Describe(code));
}
