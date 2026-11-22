using System.Diagnostics;
using System.Security.Cryptography;

namespace Bazaar.Server.DataVolume.Linux.Tests;

/// <summary>
/// A real LUKS2 container on a loop device. Runs only as root on Linux with cryptsetup and device-mapper available (the Linux
/// runners of doc 63 section 14); everywhere else it is skipped.
/// </summary>
public sealed class RealDeviceTests
{
    [Fact]
    public async Task File_container_round_trip_with_real_cryptsetup()
    {
        Assert.SkipUnless(CanRun(), "Needs root on Linux, cryptsetup on PATH and /dev/mapper/control.");
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.Combine(Path.GetTempPath(), "bazaar-luks-" + Guid.NewGuid().ToString("N")[..8]);
        var mount = Path.Combine(folder, "data");
        var container = Path.Combine(folder, "volume.img");
        Directory.CreateDirectory(mount);
        var key = RandomNumberGenerator.GetBytes(32);
        var op = new Luks2DataVolumeOperator();
        DataVolumeDescriptor? descriptor = null;
        try
        {
            var uuid = await op.CreateAsync(new DataVolumeCreateRequest("file", container, mount, 64L * 1024 * 1024, "root"), key, ct);
            descriptor = new DataVolumeDescriptor(1, "install", uuid, "luks2", "file", container, DataVolumeCiphers.XtsAes256, InstanceClass.Development, "kek", [], [], DateTimeOffset.UnixEpoch);
            await File.WriteAllTextAsync(Path.Combine(mount, "probe.txt"), "hello", ct);

            var status = await new Luks2DataVolumeProvider().InspectAsync(mount, ct);
            Assert.True(status.Mounted);
            Assert.True(status.Encrypted);
            Assert.True(status.ProtectionOn);
            Assert.Equal(uuid, status.VolumeId);
            Assert.Equal(DataVolumeCiphers.XtsAes256, status.Cipher);

            await op.CloseAsync(descriptor, mount, ct);
            Assert.False((await new Luks2DataVolumeProvider().InspectAsync(mount, ct)).Mounted);

            var wrong = RandomNumberGenerator.GetBytes(32);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await op.OpenAsync(descriptor, mount, wrong, ct));

            await op.OpenAsync(descriptor, mount, key, ct);
            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(mount, "probe.txt"), ct));
            await op.OpenAsync(descriptor, mount, key, ct);
        }
        finally
        {
            if (descriptor is not null)
            {
                try
                {
                    await op.CloseAsync(descriptor, mount, CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // Cleanup is best effort.
                }
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    private static bool CanRun()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root" || !File.Exists("/dev/mapper/control"))
            return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cryptsetup", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
