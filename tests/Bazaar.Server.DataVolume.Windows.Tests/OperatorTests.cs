namespace Bazaar.Server.DataVolume.Windows.Tests;

/// <summary>The operator's order of calls, key handling, rollback and refusals, against fake seams (doc 63 sections 3.1 and 4).</summary>
public sealed class OperatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_runs_the_steps_in_order_and_returns_the_canonical_guid()
    {
        var world = new FakeWorld();

        var id = await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct);

        Assert.Equal(FakeWorld.Guid, id);
        Assert.Equal(
            [
                "disks.create", "disks.attach", "disks.mount", "mounts.read",
                "volumes.protect", "volumes.encrypt method=7 flags=1", "volumes.conversion",
                "volumes.protection", "volumes.method", "runner.icacls.exe",
            ],
            world.Calls);
    }

    [Fact]
    public async Task Create_grants_the_service_account_modify_on_the_mount_root_through_an_argument_list()
    {
        var world = new FakeWorld();

        await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct);

        var icacls = Assert.Single(world.Runner.Commands);
        Assert.Equal([@"D:\bazaar\data\", "/grant", @"NT SERVICE\BazaarServer:(OI)(CI)M"], icacls.Arguments);
        Assert.Null(icacls.StandardInput);
    }

    [Fact]
    public async Task Create_passes_the_key_only_to_the_bitlocker_seam()
    {
        var world = new FakeWorld();

        await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct);

        Assert.Equal(world.Key, world.Volumes.ProtectedKey);
        var base64 = Convert.ToBase64String(world.Key);
        var hex = Convert.ToHexString(world.Key);
        foreach (var text in world.Calls.Concat(world.Runner.Commands.SelectMany(c => c.Arguments)))
        {
            Assert.DoesNotContain(base64, text, StringComparison.Ordinal);
            Assert.DoesNotContain(hex, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Create_refuses_an_existing_container_before_any_call()
    {
        var world = new FakeWorld();
        world.Disks.Exists = true;

        await Assert.ThrowsAsync<IOException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct));

        Assert.Empty(world.Calls);
        Assert.True(world.Disks.Exists);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Create_refuses_a_missing_or_used_mount_folder_before_any_call(int state)
    {
        var world = new FakeWorld();
        world.Disks.Folder = state == 0 ? MountFolderState.Missing : MountFolderState.NotEmpty;

        await Assert.ThrowsAnyAsync<IOException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct));

        Assert.Empty(world.Calls);
    }

    [Theory]
    [InlineData("C:\\a\"b\\x.vhdx")]
    [InlineData("C:\\a\nb\\x.vhdx")]
    [InlineData("relative\\x.vhdx")]
    [InlineData("C:\\a\\x.vhd")]
    public async Task Create_refuses_a_bad_container_path_before_any_call(string path)
    {
        var world = new FakeWorld();

        await Assert.ThrowsAsync<ArgumentException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(container: path), world.Key, Ct));

        Assert.Empty(world.Calls);
    }

    [Fact]
    public async Task Create_refuses_a_key_that_is_not_32_bytes()
    {
        var world = new FakeWorld();

        await Assert.ThrowsAsync<ArgumentException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key.AsMemory(0, 31), Ct));

        Assert.Empty(world.Calls);
    }

    public static TheoryData<string, string[]> FailureSteps() => new()
    {
        { "disks.create", ["disks.delete"] },
        { "disks.attach", ["disks.detach", "disks.delete"] },
        { "disks.mount", ["disks.unmount", "disks.detach", "disks.delete"] },
        { "volumes.protect", ["disks.unmount", "disks.detach", "disks.delete"] },
        { "volumes.encrypt method=7 flags=1", ["disks.unmount", "disks.detach", "disks.delete"] },
        { "volumes.conversion", ["disks.unmount", "disks.detach", "disks.delete"] },
        { "volumes.protection", ["disks.unmount", "disks.detach", "disks.delete"] },
        { "volumes.method", ["disks.unmount", "disks.detach", "disks.delete"] },
        { "runner.icacls.exe", ["disks.unmount", "disks.detach", "disks.delete"] },
    };

    [Theory]
    [MemberData(nameof(FailureSteps))]
    public async Task Create_rolls_back_and_leaves_nothing_when_a_step_fails(string failingCall, string[] expectedRollback)
    {
        var world = new FakeWorld { FailOn = failingCall };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct));

        Assert.Contains(failingCall, error.Message, StringComparison.Ordinal);
        var failedAt = world.Calls.IndexOf(failingCall);
        Assert.Equal(expectedRollback, world.Calls.Skip(failedAt + 1).Where(c => c.StartsWith("disks.", StringComparison.Ordinal) && c != "disks.isattached"));
        Assert.False(world.Disks.Exists);
        Assert.False(world.Disks.Attached);
    }

    [Fact]
    public async Task Create_rolls_back_when_the_volume_is_not_mounted_after_the_mount_step()
    {
        var world = new FakeWorld();
        world.Mounts.Guid = null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct));

        Assert.False(world.Disks.Exists);
        Assert.False(world.Disks.Attached);
        Assert.DoesNotContain("volumes.protect", world.Calls);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 6)]
    public async Task Create_rolls_back_when_protection_is_off_or_the_method_is_not_xts_aes_256(int protection, int method)
    {
        var world = new FakeWorld();
        world.Volumes.Protection = protection;
        world.Volumes.Method = method;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct));

        Assert.False(world.Disks.Exists);
        Assert.False(world.Disks.Attached);
        Assert.DoesNotContain("runner.icacls.exe", world.Calls);
    }

    [Fact]
    public async Task Create_cancelled_while_waiting_for_conversion_rolls_back()
    {
        var world = new FakeWorld();
        world.Volumes.Conversion.Clear();
        world.Volumes.Conversion.Enqueue(BitLockerConversion.EncryptionInProgress);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, cts.Token));

        Assert.Contains("volumes.conversion", world.Calls);
        Assert.False(world.Disks.Exists);
        Assert.False(world.Disks.Attached);
    }

    [Fact]
    public async Task Create_times_out_when_the_conversion_never_completes()
    {
        var world = new FakeWorld();
        world.Volumes.Conversion.Clear();
        world.Volumes.Conversion.Enqueue(BitLockerConversion.EncryptionInProgress);
        var options = new BitLockerVhdxOperatorOptions { PollInterval = TimeSpan.FromMilliseconds(1), ConversionTimeout = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<TimeoutException>(async () => await world.Operator(options).CreateAsync(FakeWorld.Request(), world.Key, Ct));

        Assert.False(world.Disks.Exists);
    }

    [Fact]
    public async Task Create_waits_through_an_in_progress_conversion()
    {
        var world = new FakeWorld();
        world.Volumes.Conversion.Clear();
        foreach (var status in new[] { 2, 2, 2, BitLockerConversion.FullyEncrypted })
            world.Volumes.Conversion.Enqueue(status);

        await world.Operator().CreateAsync(FakeWorld.Request(), world.Key, Ct);

        Assert.Equal(4, world.Calls.Count(c => c == "volumes.conversion"));
    }

    [Fact]
    public async Task Open_attaches_unlocks_and_verifies_protection()
    {
        var world = new FakeWorld();
        world.Volumes.Locked = 1;

        await world.Operator().OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct);

        Assert.Equal(
            ["disks.isattached", "disks.attach", "mounts.read", "volumes.lockstatus", "volumes.unlock", "volumes.protection"],
            world.Calls);
        Assert.Equal(world.Key, world.Volumes.UnlockKey);
    }

    [Fact]
    public async Task Open_is_idempotent()
    {
        var world = new FakeWorld();
        world.Volumes.Locked = 1;
        var op = world.Operator();

        await op.OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct);
        world.Calls.Clear();
        await op.OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct);

        Assert.Equal(["disks.isattached", "mounts.read", "volumes.lockstatus", "volumes.protection"], world.Calls);
    }

    [Fact]
    public async Task Open_accepts_a_descriptor_volume_id_in_decorated_form()
    {
        var world = new FakeWorld();

        await world.Operator().OpenAsync(FakeWorld.Descriptor("{" + FakeWorld.Guid.ToUpperInvariant() + "}"), FakeWorld.Mount, world.Key, Ct);

        Assert.Contains("volumes.protection", world.Calls);
    }

    [Fact]
    public async Task Open_refuses_a_different_mounted_volume_and_never_unlocks()
    {
        var world = new FakeWorld();
        world.Volumes.Locked = 1;
        world.Mounts.Guid = "11111111-2222-4333-8444-555555555555";

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct));

        Assert.DoesNotContain("volumes.unlock", world.Calls);
        Assert.False(world.Disks.Attached);
    }

    [Fact]
    public async Task Open_does_not_detach_a_disk_that_was_already_attached_when_it_fails()
    {
        var world = new FakeWorld();
        world.Disks.Attached = true;
        world.Mounts.Guid = "11111111-2222-4333-8444-555555555555";

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct));

        Assert.True(world.Disks.Attached);
    }

    [Fact]
    public async Task Open_with_a_wrong_key_fails_and_detaches_what_it_attached()
    {
        var world = new FakeWorld();
        world.Volumes.Locked = 1;
        world.Volumes.KeyIsWrong = true;

        await Assert.ThrowsAsync<BitLockerException>(async () => await world.Operator().OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct));

        Assert.False(world.Disks.Attached);
    }

    [Fact]
    public async Task Open_refuses_when_protection_is_off()
    {
        var world = new FakeWorld();
        world.Volumes.Protection = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().OpenAsync(FakeWorld.Descriptor(), FakeWorld.Mount, world.Key, Ct));
    }

    [Fact]
    public async Task Open_refuses_a_descriptor_of_another_provider()
    {
        var world = new FakeWorld();
        var other = FakeWorld.Descriptor() with { ProviderKind = "luks2" };

        await Assert.ThrowsAsync<ArgumentException>(async () => await world.Operator().OpenAsync(other, FakeWorld.Mount, world.Key, Ct));

        Assert.Empty(world.Calls);
    }

    [Fact]
    public async Task Close_locks_without_force_then_detaches()
    {
        var world = new FakeWorld();
        world.Disks.Attached = true;

        await world.Operator().CloseAsync(FakeWorld.Descriptor(), FakeWorld.Mount, Ct);

        Assert.Equal(
            ["disks.isattached", "mounts.read", "volumes.lockstatus", "volumes.lock force=False", "disks.detach"],
            world.Calls);
    }

    [Fact]
    public async Task Close_with_files_open_throws_and_never_detaches_and_never_forces()
    {
        var world = new FakeWorld();
        world.Disks.Attached = true;
        world.Volumes.LockResult = 0x80310022;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().CloseAsync(FakeWorld.Descriptor(), FakeWorld.Mount, Ct));

        Assert.IsNotType<BitLockerException>(error);
        Assert.DoesNotContain("disks.detach", world.Calls);
        Assert.DoesNotContain("volumes.lock force=True", world.Calls);
        Assert.True(world.Disks.Attached);
    }

    [Fact]
    public async Task Close_of_an_already_locked_volume_only_detaches()
    {
        var world = new FakeWorld();
        world.Disks.Attached = true;
        world.Volumes.Locked = 1;

        await world.Operator().CloseAsync(FakeWorld.Descriptor(), FakeWorld.Mount, Ct);

        Assert.DoesNotContain(world.Calls, c => c.StartsWith("volumes.lock force", StringComparison.Ordinal));
        Assert.Contains("disks.detach", world.Calls);
    }

    [Fact]
    public async Task Close_of_a_detached_volume_does_nothing()
    {
        var world = new FakeWorld();

        await world.Operator().CloseAsync(FakeWorld.Descriptor(), FakeWorld.Mount, Ct);

        Assert.Equal(["disks.isattached"], world.Calls);
    }

    [Fact]
    public async Task Close_refuses_a_different_mounted_volume()
    {
        var world = new FakeWorld();
        world.Disks.Attached = true;
        world.Mounts.Guid = null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await world.Operator().CloseAsync(FakeWorld.Descriptor(), FakeWorld.Mount, Ct));

        Assert.DoesNotContain("disks.detach", world.Calls);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("volumes.lock force", StringComparison.Ordinal));
    }
}
