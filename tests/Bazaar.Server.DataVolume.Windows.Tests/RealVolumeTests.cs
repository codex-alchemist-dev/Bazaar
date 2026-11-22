using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Bazaar.Server.DataVolume.Windows.Tests;

/// <summary>
/// One real round trip on a real VHDX. It needs Windows, an elevated process and BAZAAR_TEST_BITLOCKER_DIR naming an existing
/// folder to put the test container in. There is no default folder, and the test never touches any other path.
/// </summary>
public sealed class RealVolumeTests
{
    public const string DirectoryVariable = "BAZAAR_TEST_BITLOCKER_DIR";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_close_open_close_and_wrong_key_on_a_real_vhdx()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "BitLocker VHDX volumes exist only on Windows.");
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory), $"{DirectoryVariable} does not name an existing folder.");

        if (OperatingSystem.IsWindows())
        {
            Assert.SkipUnless(IsElevated(), "This test needs an administrator token.");
            await Run(directory!);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task Run(string directory)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var container = Path.Combine(directory, $"bazaar-it-{suffix}.vhdx");
        var mount = Path.Combine(directory, $"bazaar-it-{suffix}-mnt");
        Directory.CreateDirectory(mount);

        var op = BitLockerVhdxDataVolumeOperator.CreateDefault();
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var request = new DataVolumeCreateRequest("file", container, mount, 256L * 1024 * 1024, WindowsIdentity.GetCurrent().Name);
            var id = await op.CreateAsync(request, key, Ct);
            Assert.True(VolumeGuid.IsCanonical(id));
            var descriptor = new DataVolumeDescriptor(
                1, "it", id, op.Kind, "file", container, DataVolumeCiphers.XtsAes256, InstanceClass.Development, "kek", [], [], DateTimeOffset.UtcNow);

            await File.WriteAllTextAsync(Path.Combine(mount, "probe.txt"), "probe", Ct);
            await op.CloseAsync(descriptor, mount, Ct);

            // Re-attached: the mount comes back by itself and is locked until the right key unlocks it.
            await Assert.ThrowsAnyAsync<Exception>(async () => await op.OpenAsync(descriptor, mount, wrongKey, Ct));
            await op.OpenAsync(descriptor, mount, key, Ct);
            await op.OpenAsync(descriptor, mount, key, Ct);
            Assert.Equal("probe", await File.ReadAllTextAsync(Path.Combine(mount, "probe.txt"), Ct));
            await op.CloseAsync(descriptor, mount, Ct);

            // An existing container is never overwritten.
            await Assert.ThrowsAsync<IOException>(async () => await op.CreateAsync(request, key, Ct));
        }
        finally
        {
            await Cleanup(container, mount);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task Cleanup(string container, string mount)
    {
        var runner = new ProcessCommandRunner();
        var disks = new DiskpartVirtualDisks(runner);
        try
        {
            if (await disks.IsAttachedAsync(container, CancellationToken.None))
                await disks.DetachAsync(container, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Best effort: the container may not exist.
        }

        try
        {
            await disks.RemoveMountPointAsync(mount, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Best effort: the mount point may already be gone.
        }

        if (File.Exists(container))
            File.Delete(container);

        if (Directory.Exists(mount))
            Directory.Delete(mount, recursive: false);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
