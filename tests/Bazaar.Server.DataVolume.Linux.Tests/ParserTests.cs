namespace Bazaar.Server.DataVolume.Linux.Tests;

public sealed class ParserTests
{
    private const string _mountInfo = """
        22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw
        40 22 253:0 / /srv/bazaar-data rw,nodev,nosuid,noexec - ext4 /dev/mapper/bazaar-data-0a1b2c3d rw
        41 40 253:1 / /srv/bazaar-data/nested rw - ext4 /dev/mapper/other rw
        42 22 253:2 / /srv/my\040data rw - ext4 /dev/mapper/spaced rw
        43 22 8:1 /sub /srv/bind rw - ext4 /dev/sda1 rw
        garbage line
        """;

    [Fact]
    public void DataRoot_that_is_a_mount_point_is_found()
    {
        var entry = MountInfoParser.FindMountAt(_mountInfo, "/srv/bazaar-data");
        Assert.NotNull(entry);
        Assert.Equal("253:0", entry.MajorMinor);
        Assert.Equal("/", entry.Root);
    }

    [Fact]
    public void Trailing_slash_does_not_matter()
    {
        Assert.NotNull(MountInfoParser.FindMountAt(_mountInfo, "/srv/bazaar-data/"));
    }

    [Fact]
    public void Plain_folder_under_another_mount_is_not_a_mount_point()
    {
        Assert.Null(MountInfoParser.FindMountAt(_mountInfo, "/srv/bazaar-data/plain"));
        Assert.Null(MountInfoParser.FindMountAt(_mountInfo, "/srv"));
    }

    [Fact]
    public void Escaped_space_in_mount_point_is_decoded()
    {
        var entry = MountInfoParser.FindMountAt(_mountInfo, "/srv/my data");
        Assert.NotNull(entry);
        Assert.Equal("253:2", entry.MajorMinor);
    }

    [Fact]
    public void Nested_mount_is_found_on_its_own_path()
    {
        Assert.Equal("253:1", MountInfoParser.FindMountAt(_mountInfo, "/srv/bazaar-data/nested")?.MajorMinor);
    }

    [Fact]
    public void Bind_mount_of_a_subfolder_reports_a_non_root_root()
    {
        Assert.Equal("/sub", MountInfoParser.FindMountAt(_mountInfo, "/srv/bind")?.Root);
    }

    [Fact]
    public void Later_mount_at_the_same_point_wins()
    {
        const string text = "1 0 1:1 / /x rw - ext4 a rw\n2 0 2:2 / /x rw - ext4 b rw\n";
        Assert.Equal("2:2", MountInfoParser.FindMountAt(text, "/x")?.MajorMinor);
    }

    [Theory]
    [InlineData("a\\040b", "a b")]
    [InlineData("a\\134b", "a\\b")]
    [InlineData("a\\04", "a\\04")]
    [InlineData("a\\9zz", "a\\9zz")]
    public void Unescape_handles_octal_and_leaves_the_rest(string input, string expected)
    {
        Assert.Equal(expected, MountInfoParser.Unescape(input));
    }

    [Theory]
    [InlineData("CRYPT-LUKS2-0a1b2c3d4e5f4a6b8c7d1234567890ab-bazaar-data-0a1b2c3d", "0a1b2c3d-4e5f-4a6b-8c7d-1234567890ab")]
    [InlineData("CRYPT-LUKS2-0A1B2C3D4E5F4A6B8C7D1234567890AB-x\n", "0a1b2c3d-4e5f-4a6b-8c7d-1234567890ab")]
    [InlineData("CRYPT-LUKS1-0a1b2c3d4e5f4a6b8c7d1234567890ab-name", null)]
    [InlineData("CRYPT-PLAIN-name", null)]
    [InlineData("LVM-abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd", null)]
    [InlineData("CRYPT-LUKS2-0a1b2c3d4e5f4a6b8c7d1234567890ab", null)]
    [InlineData("CRYPT-LUKS2-0a1b2c3d4e5f4a6b8c7d1234567890zz-name", null)]
    [InlineData("CRYPT-LUKS2-0a1b2c3d4e5f4a6b8c7d1234567890abXname", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("garbage", null)]
    public void DmUuid_accepts_only_luks2(string? input, string? expected)
    {
        Assert.Equal(expected, DmUuid.TryGetLuks2Uuid(input));
    }

    [Fact]
    public void Header_with_xts_and_512_bit_key_is_xts_aes_256()
    {
        var header = HeaderBuilder.Build();
        Assert.Equal(DataVolumeCiphers.XtsAes256, Luks2Header.ReadCipher(header));
        Assert.Equal(FakeCommandRunner.Uuid, Luks2Header.ReadUuid(header));
    }

    [Theory]
    [InlineData("""{"keyslots":{"0":{"type":"luks2","key_size":64}},"segments":{"0":{"type":"crypt","encryption":"aes-cbc-essiv:sha256"}}}""")]
    [InlineData("""{"keyslots":{"0":{"type":"luks2","key_size":32}},"segments":{"0":{"type":"crypt","encryption":"aes-xts-plain64"}}}""")]
    [InlineData("""{"keyslots":{"0":{"type":"luks2","key_size":64},"1":{"type":"luks2","key_size":32}},"segments":{"0":{"type":"crypt","encryption":"aes-xts-plain64"}}}""")]
    [InlineData("""{"keyslots":{},"segments":{"0":{"type":"crypt","encryption":"aes-xts-plain64"}}}""")]
    [InlineData("""{"keyslots":{"0":{"type":"luks2","key_size":64}},"segments":{}}""")]
    [InlineData("""{"keyslots":{"0":{"type":"luks2","key_size":"64"}},"segments":{"0":{"type":"crypt","encryption":"aes-xts-plain64"}}}""")]
    [InlineData("""{"keyslots":{"0":{"type":"luks2","key_size":64}},"segments":{"0":{"type":"linear"}}}""")]
    [InlineData("[]")]
    [InlineData("{not json")]
    [InlineData("")]
    public void Weak_or_malformed_json_gives_null(string json)
    {
        Assert.Null(Luks2Header.ReadCipher(HeaderBuilder.Build(json)));
    }

    [Fact]
    public void Truncated_wrong_magic_wrong_version_and_oversized_headers_give_null()
    {
        var good = HeaderBuilder.Build();
        Assert.Null(Luks2Header.ReadCipher(good.AsSpan(0, 5000)));
        Assert.Null(Luks2Header.ReadCipher(good.AsSpan(0, 100)));
        Assert.Null(Luks2Header.ReadCipher(HeaderBuilder.Build(goodMagic: false)));
        Assert.Null(Luks2Header.ReadCipher(HeaderBuilder.Build(version: 1)));
        Assert.Null(Luks2Header.ReadCipher(HeaderBuilder.Build(size: 8UL * 1024 * 1024)));
        Assert.Null(Luks2Header.ReadCipher(HeaderBuilder.Build(size: ulong.MaxValue)));
        Assert.Null(Luks2Header.ReadCipher(HeaderBuilder.Build(size: 5000)));
        Assert.Null(Luks2Header.ReadCipher([]));
        Assert.Null(Luks2Header.ReadUuid(HeaderBuilder.Build(goodMagic: false)));
        Assert.Null(Luks2Header.ReadUuid(HeaderBuilder.Build(uuid: "not-a-uuid")));
    }

    [Theory]
    [InlineData("/dev/loop0: [64769]:12 (/srv/c.img)\n", "/dev/loop0")]
    [InlineData("/dev/sda: [1]:2 (/x)\n", null)]
    [InlineData("", null)]
    public void Losetup_output_yields_loop_devices_only(string output, string? expected)
    {
        var devices = LosetupOutput.ParseDevices(output);
        Assert.Equal(expected, devices.Count > 0 ? devices[0] : null);
    }
}
