using System.Net;
using System.Net.Sockets;
using Bazaar.Guest.PoolSpawn;

namespace Bazaar.Guest.PoolSpawn.Tests;

public sealed class ProbeConnectTests
{
    private static ProbeArguments Loopback(int port, int timeoutMs = 2000) => new([127, 0, 0, 1], port, timeoutMs);

    private static void RequireLinux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "The probe uses Linux socket calls.");

    [Fact]
    public void A_listening_port_connects_and_the_listener_sees_it()
    {
        RequireLinux();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var outcome = ProbeConnect.Run(Loopback(port));

        Assert.Equal(ProbeOutcome.Connected, outcome);
        Assert.True(listener.Pending());
    }

    [Fact]
    public void A_closed_port_is_refused()
    {
        RequireLinux();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(ProbeOutcome.Refused, ProbeConnect.Run(Loopback(port)));
    }

    [Theory]
    [InlineData(111, ProbeOutcome.Refused)]
    [InlineData(110, ProbeOutcome.TimedOut)]
    [InlineData(101, ProbeOutcome.Unreachable)]
    [InlineData(113, ProbeOutcome.Unreachable)]
    [InlineData(100, ProbeOutcome.Unreachable)]
    [InlineData(112, ProbeOutcome.Unreachable)]
    [InlineData(1, ProbeOutcome.Denied)]
    [InlineData(13, ProbeOutcome.Denied)]
    [InlineData(0, ProbeOutcome.Connected)]
    [InlineData(104, ProbeOutcome.Error)]
    [InlineData(99, ProbeOutcome.Error)]
    [InlineData(24, ProbeOutcome.Error)]
    public void Errno_values_map_to_the_exit_codes_the_host_reads(int errno, ProbeOutcome expected)
    {
        Assert.Equal(expected, ProbeConnect.Classify(errno));
    }

    [Fact]
    public void The_exit_codes_are_the_documented_ones_and_do_not_collide_with_usage_or_exec_failures()
    {
        Assert.Equal(0, (int)ProbeOutcome.Connected);
        Assert.Equal([20, 21, 22, 23], new[] { ProbeOutcome.TimedOut, ProbeOutcome.Refused, ProbeOutcome.Unreachable, ProbeOutcome.Denied }.Select(o => (int)o));
        Assert.Equal(70, (int)ProbeOutcome.Error);
        Assert.All([1, 64, 74, 126, 127], c => Assert.False(c is >= 20 and <= 23 or 70));
    }
}
