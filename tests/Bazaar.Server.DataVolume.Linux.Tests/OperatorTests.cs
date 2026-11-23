using System.Security.Cryptography;

namespace Bazaar.Server.DataVolume.Linux.Tests;

public sealed class OperatorTests
{
    private const string _mount = "/srv/bazaar-data";
    private const string _container = "/srv/boot/volume.img";
    private const string _name = "bazaar-data-0a1b2c3d";
    private const string _mounted = "40 22 253:0 / /srv/bazaar-data rw - ext4 /dev/mapper/x rw\n";

    private readonly FakeCommandRunner _runner = new();
    private readonly FakeLinuxFiles _files = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private Luks2DataVolumeOperator Operator => new(_runner, _files);

    private static DataVolumeDescriptor Descriptor(string kind = "file", string path = _container) => new(
        1, "install", FakeCommandRunner.Uuid, "luks2", kind, path, DataVolumeCiphers.XtsAes256, InstanceClass.Production,
        "kek", [], [], DateTimeOffset.UnixEpoch);

    private static DataVolumeCreateRequest Request(string kind = "file", string path = _container, string mount = _mount, long size = 64L * 1024 * 1024, string account = "bazaar") =>
        new(kind, path, mount, size, account);

    private OperatorTests PrepareMountFolder()
    {
        _files.EmptyDirectories.Add(_mount);
        return this;
    }

    private async Task AssertKeyOnlyOnCryptsetupStdin()
    {
        await Task.CompletedTask;
        var hex = Convert.ToHexString(_key);
        var base64 = Convert.ToBase64String(_key);
        foreach (var call in _runner.Calls)
        {
            var args = string.Join('\n', call.Arguments);
            Assert.DoesNotContain(hex, args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(base64, args, StringComparison.Ordinal);
            var carriesKey = call.StandardInput.AsSpan().SequenceEqual(_key);
            Assert.Equal(carriesKey, call.FileName == "cryptsetup" && call.StandardInput.Length > 0);
            if (call.StandardInput.Length > 0)
                Assert.Contains("--key-file -", call.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Create_file_container_runs_the_exact_sequence()
    {
        PrepareMountFolder();

        var uuid = await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken);

        Assert.Equal(FakeCommandRunner.Uuid, uuid);
        Assert.Equal(
            [
                "truncate --size=67108864 /srv/boot/volume.img",
                "chgrp bazaar /srv/boot/volume.img",
                "losetup --find --show /srv/boot/volume.img",
                "cryptsetup luksFormat --type luks2 --cipher aes-xts-plain64 --key-size 512 --pbkdf pbkdf2 --pbkdf-force-iterations 1000 --batch-mode --key-file - --keyfile-size 32 /dev/loop7",
                "cryptsetup luksUUID /dev/loop7",
                "cryptsetup open --type luks2 --batch-mode --key-file - --keyfile-size 32 /dev/loop7 " + _name,
                "mkfs.ext4 -q -L bazaar-data /dev/mapper/" + _name,
                "mount -t ext4 -o nodev,nosuid,noexec /dev/mapper/" + _name + " /srv/bazaar-data",
                "chown bazaar /srv/bazaar-data",
                "chmod 0700 /srv/bazaar-data",
            ],
            _runner.Calls.Select(c => c.Line));
        Assert.Equal([_container], _files.Created);
        await AssertKeyOnlyOnCryptsetupStdin();
    }

    [Fact]
    public async Task Create_device_container_checks_for_signatures_and_uses_the_device()
    {
        PrepareMountFolder();

        await Operator.CreateAsync(Request("device", "/dev/sdb1"), _key, TestContext.Current.CancellationToken);

        Assert.Equal("blkid -p /dev/sdb1", _runner.Calls[0].Line);
        Assert.Contains(_runner.Calls, c => c.Line.EndsWith("--keyfile-size 32 /dev/sdb1", StringComparison.Ordinal) && c.Line.StartsWith("cryptsetup luksFormat", StringComparison.Ordinal));
        Assert.DoesNotContain(_runner.Calls, c => c.FileName is "losetup" or "truncate");
        Assert.Empty(_files.Created);
        await AssertKeyOnlyOnCryptsetupStdin();
    }

    [Fact]
    public async Task Create_refuses_a_device_that_has_a_signature()
    {
        PrepareMountFolder();
        _runner.Respond = c => c.FileName == "blkid" ? FakeCommandRunner.Ok("TYPE=\"ext4\"\n") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request("device", "/dev/sdb1"), _key, TestContext.Current.CancellationToken));

        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task Create_refuses_an_existing_container_before_any_command_runs()
    {
        PrepareMountFolder();
        _files.ExistingFiles.Add(_container);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Calls);
        Assert.Empty(_files.Deleted);
        Assert.Contains(_container, _files.ExistingFiles);
    }

    [Fact]
    public async Task Create_refuses_a_missing_or_busy_mount_folder_and_an_existing_mount_point()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken));

        PrepareMountFolder();
        _files.Texts["/proc/self/mountinfo"] = _mounted;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("relative/volume.img")]
    [InlineData("/srv/../etc/volume.img")]
    [InlineData("/srv/vol\nume.img")]
    [InlineData("/srv/vol\0ume.img")]
    [InlineData("")]
    public async Task Bad_container_paths_are_refused(string path)
    {
        PrepareMountFolder();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.CreateAsync(Request(path: path), _key, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("mount")]
    [InlineData("/srv/../data")]
    [InlineData("/srv/da\rta")]
    public async Task Bad_mount_paths_are_refused(string mount)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.OpenAsync(Descriptor(), mount, _key, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.CloseAsync(Descriptor(), mount, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("-rf")]
    [InlineData("root; id")]
    [InlineData("")]
    public async Task Bad_service_accounts_are_refused(string account)
    {
        PrepareMountFolder();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.CreateAsync(Request(account: account), _key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bad_key_length_and_small_container_are_refused()
    {
        PrepareMountFolder();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.CreateAsync(Request(), new byte[16], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.CreateAsync(Request(size: 1024), _key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failing_mkfs_stops_the_sequence_and_cleans_up_what_was_created()
    {
        PrepareMountFolder();
        _runner.Respond = c => c.FileName == "mkfs.ext4" ? FakeCommandRunner.Fail(1, "no space") : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken));

        Assert.Contains("mkfs.ext4", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.FileName is "mount" or "chown" or "chmod");
        Assert.Equal(
            ["cryptsetup close " + _name, "losetup --detach /dev/loop7"],
            _runner.Calls.Skip(_runner.Calls.FindIndex(c => c.FileName == "mkfs.ext4") + 1).Select(c => c.Line));
        Assert.Equal([_container], _files.Deleted);
        Assert.DoesNotContain(_container, _files.ExistingFiles);
        await AssertKeyOnlyOnCryptsetupStdin();
    }

    [Fact]
    public async Task Failing_mount_unwinds_the_mapping_loop_and_file()
    {
        PrepareMountFolder();
        _runner.Respond = c => c.FileName == "mount" ? FakeCommandRunner.Fail(32, "bad") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken));

        Assert.True(_runner.Ran("cryptsetup close " + _name));
        Assert.True(_runner.Ran("losetup --detach /dev/loop7"));
        Assert.Equal([_container], _files.Deleted);
    }

    [Fact]
    public async Task Failing_format_of_a_device_wipes_the_signature_it_wrote()
    {
        PrepareMountFolder();
        _runner.Respond = c => c.Line.StartsWith("cryptsetup luksFormat", StringComparison.Ordinal) ? FakeCommandRunner.Fail(1, "boom") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request("device", "/dev/sdb1"), _key, TestContext.Current.CancellationToken));

        Assert.True(_runner.Ran("wipefs --all /dev/sdb1"));
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Failing_truncate_removes_the_file_it_created()
    {
        PrepareMountFolder();
        _runner.Respond = c => c.FileName == "truncate" ? FakeCommandRunner.Fail(1, "disk full") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CreateAsync(Request(), _key, TestContext.Current.CancellationToken));

        Assert.Equal([_container], _files.Deleted);
        Assert.DoesNotContain(_runner.Calls, c => c.FileName == "cryptsetup");
    }

    [Fact]
    public async Task Open_attaches_checks_the_uuid_unlocks_and_mounts()
    {
        _files.EmptyDirectories.Add(_mount);

        await Operator.OpenAsync(Descriptor(), _mount, _key, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "cryptsetup status " + _name,
                "losetup --associated " + _container,
                "losetup --find --show " + _container,
                "cryptsetup luksUUID /dev/loop7",
                "cryptsetup open --type luks2 --batch-mode --key-file - --keyfile-size 32 /dev/loop7 " + _name,
                "mount -t ext4 -o nodev,nosuid,noexec /dev/mapper/" + _name + " /srv/bazaar-data",
            ],
            _runner.Calls.Select(c => c.Line));
        await AssertKeyOnlyOnCryptsetupStdin();
    }

    [Fact]
    public async Task Open_is_idempotent_when_mapped_and_mounted()
    {
        _runner.MappingActive = true;
        _files.Texts["/proc/self/mountinfo"] = _mounted;

        await Operator.OpenAsync(Descriptor(), _mount, _key, TestContext.Current.CancellationToken);

        Assert.Equal(["cryptsetup status " + _name], _runner.Calls.Select(c => c.Line));
    }

    [Fact]
    public async Task Open_only_mounts_when_the_mapping_is_already_active()
    {
        _runner.MappingActive = true;
        _files.EmptyDirectories.Add(_mount);

        await Operator.OpenAsync(Descriptor(), _mount, _key, TestContext.Current.CancellationToken);

        Assert.Equal(["cryptsetup status " + _name, "mount -t ext4 -o nodev,nosuid,noexec /dev/mapper/" + _name + " /srv/bazaar-data"], _runner.Calls.Select(c => c.Line));
    }

    [Fact]
    public async Task Open_refuses_a_container_with_another_uuid()
    {
        _files.EmptyDirectories.Add(_mount);
        _runner.Respond = c => c.Line.StartsWith("cryptsetup luksUUID", StringComparison.Ordinal) ? FakeCommandRunner.Ok("ffffffff-ffff-4fff-8fff-ffffffffffff\n") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.OpenAsync(Descriptor(), _mount, _key, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(_runner.Calls, c => c.Line.StartsWith("cryptsetup open", StringComparison.Ordinal));
        Assert.True(_runner.Ran("losetup --detach /dev/loop7"));
    }

    [Fact]
    public async Task Open_with_a_wrong_key_detaches_the_loop_it_attached_and_leaves_no_mapping()
    {
        _files.EmptyDirectories.Add(_mount);
        _runner.Respond = c => c.Line.StartsWith("cryptsetup open", StringComparison.Ordinal) ? FakeCommandRunner.Fail(2, "No key available with this passphrase.") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.OpenAsync(Descriptor(), _mount, _key, TestContext.Current.CancellationToken));

        Assert.True(_runner.Ran("losetup --detach /dev/loop7"));
        Assert.DoesNotContain(_runner.Calls, c => c.FileName == "mount");
        Assert.DoesNotContain(_runner.Calls, c => c.Line.StartsWith("cryptsetup close", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Open_failing_mount_closes_the_mapping_it_opened()
    {
        _files.EmptyDirectories.Add(_mount);
        _runner.Respond = c => c.FileName == "mount" ? FakeCommandRunner.Fail(32, "bad") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.OpenAsync(Descriptor(), _mount, _key, TestContext.Current.CancellationToken));

        Assert.True(_runner.Ran("cryptsetup close " + _name));
        Assert.True(_runner.Ran("losetup --detach /dev/loop7"));
    }

    [Fact]
    public async Task Close_unmounts_closes_and_detaches_the_loop()
    {
        _runner.MappingActive = true;
        _runner.LoopAttached = true;
        _files.Texts["/proc/self/mountinfo"] = _mounted;

        await Operator.CloseAsync(Descriptor(), _mount, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["umount /srv/bazaar-data", "cryptsetup status " + _name, "cryptsetup close " + _name, "losetup --associated " + _container, "losetup --detach /dev/loop7"],
            _runner.Calls.Select(c => c.Line));
        Assert.DoesNotContain(_runner.Calls, c => c.Arguments.Any(a => a is "-l" or "-f" or "--lazy" or "--force"));
    }

    [Fact]
    public async Task Close_of_a_busy_mount_is_refused_and_never_closes_the_mapping()
    {
        _runner.MappingActive = true;
        _files.Texts["/proc/self/mountinfo"] = _mounted;
        _runner.Respond = c => c.FileName == "umount" ? FakeCommandRunner.Fail(32, "umount: /srv/bazaar-data: target is busy.") : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.CloseAsync(Descriptor(), _mount, TestContext.Current.CancellationToken));

        Assert.Contains("in use", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["umount /srv/bazaar-data"], _runner.Calls.Select(c => c.Line));
    }

    [Fact]
    public async Task Close_when_already_closed_does_nothing_harmful()
    {
        await Operator.CloseAsync(Descriptor("device", "/dev/sdb1"), _mount, TestContext.Current.CancellationToken);

        Assert.Equal(["cryptsetup status " + _name], _runner.Calls.Select(c => c.Line));
    }

    [Fact]
    public async Task Grow_extends_file_loop_mapping_and_file_system()
    {
        _runner.MappingActive = true;
        _runner.LoopAttached = true;
        _files.Texts["/proc/self/mountinfo"] = _mounted;

        await Operator.GrowAsync(Descriptor(), _mount, 128L * 1024 * 1024, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "cryptsetup status " + _name,
                "losetup --associated " + _container,
                "truncate --size=134217728 " + _container,
                "losetup --set-capacity /dev/loop7",
                "cryptsetup resize " + _name,
                "resize2fs /dev/mapper/" + _name,
            ],
            _runner.Calls.Select(c => c.Line));
    }

    [Fact]
    public async Task Grow_refuses_shrinking_a_device_container_and_a_closed_volume()
    {
        _runner.MappingActive = true;
        _runner.LoopAttached = true;
        _files.Texts["/proc/self/mountinfo"] = _mounted;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.GrowAsync(Descriptor(), _mount, 1024, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.GrowAsync(Descriptor("device", "/dev/sdb1"), _mount, 1L << 40, TestContext.Current.CancellationToken));

        _files.Texts.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Operator.GrowAsync(Descriptor(), _mount, 1L << 40, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(_runner.Calls, c => c.FileName is "truncate" or "resize2fs");
    }

    [Fact]
    public async Task Descriptor_of_another_provider_is_refused()
    {
        var other = Descriptor() with { ProviderKind = "bitlocker-vhdx" };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operator.OpenAsync(other, _mount, _key, TestContext.Current.CancellationToken));

        Assert.Empty(_runner.Calls);
    }
}
