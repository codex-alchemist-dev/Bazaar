namespace Bazaar.Guest.Agent.Tests;

/// <summary>The <c>--transport</c> option and the socket settings that go with it.</summary>
public class TransportOptionsTests
{
    private static readonly string[] _required = ["--vm-id", "vm-1", "--nonce", "n-1", "--root", Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bazaar-guest-root"))];

    private static GuestAgentOptions Parse(params string[] extra) => GuestAgentOptions.Parse([.. _required, .. extra]);

    [Fact]
    public void Stdio_is_the_default_transport()
    {
        var options = Parse();

        Assert.Equal(GuestTransportKind.Stdio, options.Transport);
    }

    [Theory]
    [InlineData("stdio", GuestTransportKind.Stdio)]
    [InlineData("hvsock", GuestTransportKind.HvSocket)]
    [InlineData("vsock", GuestTransportKind.Vsock)]
    public void Transport_selects_the_named_kind(string name, GuestTransportKind expected)
    {
        Assert.Equal(expected, Parse("--transport", name).Transport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HVSOCK")]
    [InlineData("tcp")]
    public void An_unknown_transport_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => Parse("--transport", name));
    }

    [Fact]
    public void Socket_defaults_match_the_host_listener_defaults()
    {
        var options = Parse("--transport", "vsock");

        Assert.Equal(new Guid("00001388-facb-11e6-bd58-64006a7986d3"), options.HvSocketServiceId);
        Assert.Equal(5000u, options.VsockPort);
        Assert.Equal(2u, options.VsockHostCid);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ConnectTimeout);
    }

    [Fact]
    public void Socket_constants_come_from_the_options()
    {
        var options = Parse(
            "--transport", "hvsock",
            "--hvsock-service-id", "6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60",
            "--vsock-port", "6001",
            "--vsock-cid", "3",
            "--connect-timeout-seconds", "7");

        Assert.Equal(new Guid("6f1c3b7a-2d48-4f6e-9a35-1b2c3d4e5f60"), options.HvSocketServiceId);
        Assert.Equal(6001u, options.VsockPort);
        Assert.Equal(3u, options.VsockHostCid);
        Assert.Equal(TimeSpan.FromSeconds(7), options.ConnectTimeout);
    }

    [Theory]
    [InlineData("--hvsock-service-id", "not-a-guid")]
    [InlineData("--hvsock-service-id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("--vsock-port", "0")]
    [InlineData("--vsock-port", "-1")]
    [InlineData("--vsock-cid", "abc")]
    [InlineData("--connect-timeout-seconds", "0")]
    public void A_bad_socket_setting_is_refused(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => Parse(name, value));
    }

    [Fact]
    public void The_transport_option_needs_a_value()
    {
        Assert.Throws<ArgumentException>(() => Parse("--transport"));
    }
}
