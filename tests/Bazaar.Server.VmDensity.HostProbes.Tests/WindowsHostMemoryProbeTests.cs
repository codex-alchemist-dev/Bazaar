using System.Runtime.Versioning;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.VmDensity.HostProbes;

namespace Bazaar.Server.VmDensity.HostProbes.Tests;

[SupportedOSPlatform("windows")]
public class WindowsHostMemoryProbeTests
{
    private sealed class FakeSource(WindowsMemoryStatus? status) : IWindowsMemoryStatusSource
    {
        public WindowsMemoryStatus Read() => status ?? throw new HostMemoryProbeException("native call failed");
    }

    [Fact]
    public async Task MapsBytesToFlooredMib()
    {
        var probe = new WindowsHostMemoryProbe(new FakeSource(new WindowsMemoryStatus(17_179_869_184UL + 5, (4UL * 1024 * 1024 * 1024) + 1_000_000)));
        Assert.Equal(new HostMemory(16384, 4096), await probe.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(100UL, 200UL)]
    public async Task InconsistentValuesThrow(ulong total, ulong avail)
    {
        var probe = new WindowsHostMemoryProbe(new FakeSource(new WindowsMemoryStatus(total, avail)));
        await Assert.ThrowsAsync<HostMemoryProbeException>(() => probe.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NativeFailurePropagates()
    {
        var probe = new WindowsHostMemoryProbe(new FakeSource(null));
        await Assert.ThrowsAsync<HostMemoryProbeException>(() => probe.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var probe = new WindowsHostMemoryProbe(new FakeSource(new WindowsMemoryStatus(1UL << 30, 1UL << 29)));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task RealNativeCallReturnsSaneValuesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        HostMemory hm = await new WindowsHostMemoryProbe().ReadAsync(CancellationToken.None);
        Assert.True(hm.TotalMb > 0);
    }
}
