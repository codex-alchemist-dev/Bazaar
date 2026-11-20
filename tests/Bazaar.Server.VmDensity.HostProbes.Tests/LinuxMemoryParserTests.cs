using Bazaar.Server.VmDensity.HostProbes;

namespace Bazaar.Server.VmDensity.HostProbes.Tests;

public class LinuxMemoryParserTests
{
    private const string _good = "MemTotal:       16384000 kB\nMemFree:          100000 kB\nMemAvailable:    8192000 kB\nSwapCached: 0 kB\nSwapTotal:       2097152 kB\nSwapFree:        1048576 kB\n";

    [Fact]
    public void ParsesMeminfoToBytes()
    {
        MeminfoValues v = LinuxMemoryParser.ParseMeminfo(_good);
        Assert.Equal(16384000L * 1024, v.MemTotalBytes);
        Assert.Equal(8192000L * 1024, v.MemAvailableBytes);
        Assert.Equal(2097152L * 1024, v.SwapTotalBytes);
        Assert.Equal(1048576L * 1024, v.SwapFreeBytes);
    }

    [Fact]
    public void ToHostMemoryFloorsToMib()
    {
        var hm = LinuxMemoryParser.ToHostMemory(LinuxMemoryParser.ParseMeminfo(_good));
        Assert.Equal(16000, hm.TotalMb);
        Assert.Equal(8000, hm.FreeMb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MemTotal: 100 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: abc kB\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: -5 kB\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: 100 MB\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: 100\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: 0 kB\nMemAvailable: 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: 100 kB\nMemAvailable: 200 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    [InlineData("MemTotal: 100 kB\nMemAvailable: 1 kB\nSwapTotal: 1 kB\nSwapFree: 2 kB\n")]
    [InlineData("MemTotal: 100 kB\nMemTotal: 100 kB\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")]
    public void MalformedMeminfoThrows(string text) =>
        Assert.Throws<HostMemoryProbeException>(() => LinuxMemoryParser.ParseMeminfo(text));

    [Fact]
    public void KbOverflowThrows() =>
        Assert.Throws<HostMemoryProbeException>(() => LinuxMemoryParser.ParseMeminfo(
            "MemTotal: 9223372036854775807 kB\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n"));

    [Fact]
    public void ParsesPressure()
    {
        MemoryPressure p = LinuxMemoryParser.ParsePressure(
            "some avg10=1.50 avg60=0.40 avg300=0.10 total=12345\nfull avg10=0.25 avg60=0.00 avg300=0.00 total=99\n");
        Assert.Equal(1.5, p.SomeAvg10);
        Assert.Equal(0.25, p.FullAvg10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("some avg10=1.0 avg60=0 avg300=0 total=1\n")]
    [InlineData("some avg10=x avg60=0\nfull avg10=0 avg60=0\n")]
    [InlineData("some avg10=NaN\nfull avg10=0\n")]
    [InlineData("some avg10=150\nfull avg10=0\n")]
    [InlineData("some avg60=1\nfull avg10=0\n")]
    public void MalformedPressureThrows(string text) =>
        Assert.Throws<HostMemoryProbeException>(() => LinuxMemoryParser.ParsePressure(text));
}
