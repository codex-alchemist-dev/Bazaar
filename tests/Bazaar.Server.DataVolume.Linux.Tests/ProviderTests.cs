namespace Bazaar.Server.DataVolume.Linux.Tests;

public sealed class ProviderTests
{
    private const string _root = "/srv/bazaar-data";
    private const string _dev = "/sys/dev/block/253:0";

    private static FakeLinuxFiles HappyFiles()
    {
        var files = new FakeLinuxFiles();
        files.Texts["/proc/self/mountinfo"] = "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n40 22 253:0 / /srv/bazaar-data rw - ext4 /dev/mapper/x rw\n";
        files.Texts[_dev + "/dm/uuid"] = "CRYPT-LUKS2-0a1b2c3d4e5f4a6b8c7d1234567890ab-bazaar-data-0a1b2c3d\n";
        files.Directories[_dev + "/slaves"] = ["loop7"];
        files.Texts["/sys/block/loop7/loop/backing_file"] = "/srv/boot/volume.img\n";
        files.Prefixes["/srv/boot/volume.img"] = HeaderBuilder.Build();
        return files;
    }

    private static ValueTask<DataVolumeStatus> Inspect(FakeLinuxFiles files, string root = _root) =>
        new Luks2DataVolumeProvider(files).InspectAsync(root, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Happy_path_reports_encrypted_xts_volume_with_its_uuid()
    {
        var status = await Inspect(HappyFiles());

        Assert.Equal(new DataVolumeStatus(true, FakeCommandRunner.Uuid, true, true, DataVolumeCiphers.XtsAes256), status);
    }

    [Fact]
    public async Task Plain_folder_is_not_mounted()
    {
        var status = await Inspect(HappyFiles(), _root + "/sub");

        Assert.False(status.Mounted);
        Assert.False(status.Encrypted);
        Assert.Null(status.VolumeId);
    }

    [Fact]
    public async Task Bind_mount_of_a_subfolder_is_not_the_root_of_a_volume()
    {
        var files = HappyFiles();
        files.Texts["/proc/self/mountinfo"] = "40 22 253:0 /sub /srv/bazaar-data rw - ext4 /dev/mapper/x rw\n";

        Assert.False((await Inspect(files)).Mounted);
    }

    [Theory]
    [InlineData("CRYPT-LUKS1-0a1b2c3d4e5f4a6b8c7d1234567890ab-x")]
    [InlineData("CRYPT-PLAIN-x")]
    [InlineData("LVM-abc")]
    [InlineData(null)]
    public async Task Non_luks2_device_is_mounted_but_not_encrypted(string? dmUuid)
    {
        var files = HappyFiles();
        if (dmUuid is null)
            files.Texts.Remove(_dev + "/dm/uuid");
        else
            files.Texts[_dev + "/dm/uuid"] = dmUuid;

        var status = await Inspect(files);

        Assert.True(status.Mounted);
        Assert.False(status.Encrypted);
        Assert.False(status.ProtectionOn);
        Assert.Null(status.Cipher);
    }

    [Fact]
    public async Task Unreadable_header_leaves_the_cipher_null_so_the_guard_refuses()
    {
        var files = HappyFiles();
        files.Prefixes.Clear();

        var status = await Inspect(files);

        Assert.True(status.Encrypted);
        Assert.Equal(FakeCommandRunner.Uuid, status.VolumeId);
        Assert.Null(status.Cipher);
    }

    [Fact]
    public async Task Header_of_another_container_does_not_vouch_for_the_mapping()
    {
        var files = HappyFiles();
        files.Prefixes["/srv/boot/volume.img"] = HeaderBuilder.Build(uuid: "ffffffff-ffff-4fff-8fff-ffffffffffff");

        Assert.Null((await Inspect(files)).Cipher);
    }

    [Fact]
    public async Task Weak_header_gives_no_cipher()
    {
        var files = HappyFiles();
        files.Prefixes["/srv/boot/volume.img"] = HeaderBuilder.Build("""{"keyslots":{"0":{"type":"luks2","key_size":32}},"segments":{"0":{"type":"crypt","encryption":"aes-xts-plain64"}}}""");

        Assert.Null((await Inspect(files)).Cipher);
    }

    [Fact]
    public async Task Block_device_backing_is_read_from_dev()
    {
        var files = HappyFiles();
        files.Directories[_dev + "/slaves"] = ["sdb1"];
        files.Texts.Remove("/sys/block/sdb1/loop/backing_file");
        files.Prefixes["/dev/sdb1"] = HeaderBuilder.Build();

        Assert.Equal(DataVolumeCiphers.XtsAes256, (await Inspect(files)).Cipher);
    }

    [Fact]
    public async Task Stacked_mapping_gives_no_cipher()
    {
        var files = HappyFiles();
        files.Directories[_dev + "/slaves"] = ["loop7", "loop8"];

        Assert.Null((await Inspect(files)).Cipher);
    }

    [Fact]
    public async Task Missing_mountinfo_reads_as_not_mounted()
    {
        var files = HappyFiles();
        files.Texts.Clear();

        Assert.False((await Inspect(files)).Mounted);
    }

    [Fact]
    public void Component_manifest_names_the_data_volume_dependency()
    {
        var manifest = new LinuxDataVolumeComponent().Manifest;

        Assert.Equal("data-volume-linux", manifest.Id);
        Assert.Equal(["data-volume"], manifest.DependsOn);
    }
}
