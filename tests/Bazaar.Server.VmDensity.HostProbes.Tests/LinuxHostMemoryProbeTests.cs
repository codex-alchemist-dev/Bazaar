using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.VmDensity.HostProbes;

namespace Bazaar.Server.VmDensity.HostProbes.Tests;

public sealed class LinuxHostMemoryProbeTests : IDisposable
{
    private const string _meminfo = "MemTotal: 2097152 kB\nMemAvailable: 1048576 kB\nSwapTotal: 1024 kB\nSwapFree: 512 kB\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-probe-" + Guid.NewGuid().ToString("N"));

    public LinuxHostMemoryProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string name, string text, bool pressureDir = false)
    {
        string dir = pressureDir ? Path.Combine(_root, "pressure") : _root;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text);
    }

    [Fact]
    public async Task ReadsMemoryWithoutPressureFile()
    {
        Write("meminfo", _meminfo);
        LinuxMemorySample s = await new LinuxHostMemoryProbe(_root).ReadDetailedAsync(CancellationToken.None);
        Assert.Equal(new HostMemory(2048, 1024), s.Memory);
        Assert.Equal(1024 * 1024, s.SwapTotalBytes);
        Assert.Equal(512 * 1024, s.SwapFreeBytes);
        Assert.Null(s.Pressure);
    }

    [Fact]
    public async Task ReadAsyncReturnsHostMemory()
    {
        Write("meminfo", _meminfo);
        Assert.Equal(new HostMemory(2048, 1024), await new LinuxHostMemoryProbe(_root).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadsPressureWhenPresent()
    {
        Write("meminfo", _meminfo);
        Write("memory", "some avg10=2.00 avg60=0 avg300=0 total=0\nfull avg10=1.00 avg60=0 avg300=0 total=0\n", pressureDir: true);
        LinuxMemorySample s = await new LinuxHostMemoryProbe(_root).ReadDetailedAsync(CancellationToken.None);
        Assert.Equal(new MemoryPressure(2.0, 1.0), s.Pressure);
    }

    [Fact]
    public async Task MalformedPressureThrows()
    {
        Write("meminfo", _meminfo);
        Write("memory", "garbage", pressureDir: true);
        await Assert.ThrowsAsync<HostMemoryProbeException>(() => new LinuxHostMemoryProbe(_root).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingMeminfoThrows() =>
        await Assert.ThrowsAsync<HostMemoryProbeException>(() => new LinuxHostMemoryProbe(_root).ReadAsync(CancellationToken.None));

    [Fact]
    public async Task OversizedFileThrows()
    {
        Write("meminfo", _meminfo + new string('x', 70 * 1024));
        await Assert.ThrowsAsync<HostMemoryProbeException>(() => new LinuxHostMemoryProbe(_root).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        Write("meminfo", _meminfo);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LinuxHostMemoryProbe(_root).ReadAsync(cts.Token));
    }

    [Fact]
    public async Task RealProcReadsOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        HostMemory hm = await new LinuxHostMemoryProbe().ReadAsync(CancellationToken.None);
        Assert.True(hm.TotalMb > 0);
        Assert.InRange(hm.FreeMb, 0, hm.TotalMb);
    }
}
