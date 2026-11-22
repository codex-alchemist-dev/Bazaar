namespace Bazaar.Server.DataVolume.Windows.Tests;

/// <summary>The unprivileged provider with a fake mount reader and a fake status channel: it must fail closed.</summary>
public sealed class ProviderTests
{
    private const string _guid = FakeWorld.Guid;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VolumeStatusReply Reply(string? volume = _guid, bool complete = true, bool protection = true, int method = 7, bool locked = false) =>
        new(volume, complete, protection, method, locked);

    private static BitLockerVhdxDataVolumeProvider Provider(string? mountGuid, VolumeStatusReply? reply) =>
        new(new StubMounts(mountGuid), new StubChannel(reply));

    [Fact]
    public async Task Kind_is_bitlocker_vhdx()
    {
        Assert.Equal("bitlocker-vhdx", Provider(_guid, null).Kind);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Happy_path_reports_mounted_encrypted_protected_xts_aes_256()
    {
        var status = await Provider(_guid, Reply()).InspectAsync(FakeWorld.Mount, Ct);

        Assert.Equal(new DataVolumeStatus(true, _guid, true, true, DataVolumeCiphers.XtsAes256), status);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_mount_point_is_not_mounted_and_the_channel_is_not_asked()
    {
        var channel = new StubChannel(Reply());

        var status = await new BitLockerVhdxDataVolumeProvider(new StubMounts(null), channel).InspectAsync(FakeWorld.Mount, Ct);

        Assert.Equal(new DataVolumeStatus(false, null, false, false, null), status);
        Assert.Equal(0, channel.Asked);
    }

    [Fact]
    public async Task An_unavailable_channel_fails_closed()
    {
        var status = await Provider(_guid, null).InspectAsync(FakeWorld.Mount, Ct);

        Assert.True(status.Mounted);
        Assert.Equal(_guid, status.VolumeId);
        Assert.False(status.Encrypted);
        Assert.False(status.ProtectionOn);
        Assert.Null(status.Cipher);
    }

    [Fact]
    public async Task An_answer_for_another_volume_fails_closed()
    {
        var status = await Provider(_guid, Reply(volume: "11111111-2222-4333-8444-555555555555")).InspectAsync(FakeWorld.Mount, Ct);

        Assert.False(status.Encrypted);
        Assert.False(status.ProtectionOn);
        Assert.Null(status.Cipher);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(4)]
    [InlineData(3)]
    public async Task A_method_other_than_7_gives_a_cipher_below_the_floor(int method)
    {
        var status = await Provider(_guid, Reply(method: method)).InspectAsync(FakeWorld.Mount, Ct);

        Assert.True(status.Encrypted);
        Assert.False(DataVolumeCiphers.MeetsFloor(status.Cipher));
        Assert.NotEqual(DataVolumeCiphers.XtsAes256, status.Cipher);
    }

    [Fact]
    public async Task Method_0_is_reported_as_no_cipher()
    {
        var status = await Provider(_guid, Reply(method: 0)).InspectAsync(FakeWorld.Mount, Ct);

        Assert.Equal(DataVolumeCiphers.None, status.Cipher);
    }

    [Fact]
    public async Task A_conversion_in_progress_is_not_encrypted()
    {
        var status = await Provider(_guid, Reply(complete: false)).InspectAsync(FakeWorld.Mount, Ct);

        Assert.False(status.Encrypted);
        Assert.Null(status.Cipher);
    }

    [Fact]
    public async Task Protection_off_is_reported_as_it_is()
    {
        var status = await Provider(_guid, Reply(protection: false)).InspectAsync(FakeWorld.Mount, Ct);

        Assert.True(status.Encrypted);
        Assert.False(status.ProtectionOn);
    }

    // Decision (documented on the provider): a locked volume is the right volume and is encrypted. Reads fail, so the
    // guard's marker check is what refuses the start.
    [Fact]
    public async Task A_locked_volume_reports_mounted_and_encrypted()
    {
        var status = await Provider(_guid, Reply(locked: true)).InspectAsync(FakeWorld.Mount, Ct);

        Assert.Equal(new DataVolumeStatus(true, _guid, true, true, DataVolumeCiphers.XtsAes256), status);
    }

    private sealed class StubMounts(string? guid) : IMountPointReader
    {
        public string? TryGetVolumeGuid(string folder) => guid;
    }

    private sealed class StubChannel(VolumeStatusReply? reply) : IVolumeStatusChannel
    {
        public int Asked { get; private set; }

        public ValueTask<VolumeStatusReply?> GetStatusAsync(string canonicalVolumeGuid, CancellationToken cancellationToken)
        {
            Asked++;
            return ValueTask.FromResult(reply);
        }
    }
}
