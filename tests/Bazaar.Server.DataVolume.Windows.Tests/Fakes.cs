using System.Security.Cryptography;

namespace Bazaar.Server.DataVolume.Windows.Tests;

/// <summary>The seams as fakes. They record every call as text in one shared list, so tests assert the exact order.</summary>
internal sealed class FakeWorld
{
    public const string Guid = "0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70";
    public const string Container = @"D:\bazaar\data.vhdx";
    public const string Mount = @"D:\bazaar\data";

    public FakeWorld()
    {
        Key = new byte[32];
        RandomNumberGenerator.Fill(Key);
        Disks = new FakeDisks(this);
        Volumes = new FakeBitLocker(this);
        Mounts = new FakeMounts(this);
        Runner = new FakeRunner(this);
    }

    public List<string> Calls { get; } = [];

    /// <summary>The call name that throws <see cref="InvalidOperationException"/> when reached, for example <c>disks.attach</c>.</summary>
    public string? FailOn { get; set; }

    public byte[] Key { get; }

    public FakeDisks Disks { get; }

    public FakeBitLocker Volumes { get; }

    public FakeMounts Mounts { get; }

    public FakeRunner Runner { get; }

    public BitLockerVhdxOperatorOptions FastOptions { get; } = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        ConversionTimeout = TimeSpan.FromSeconds(10),
        MountAttempts = 3,
    };

    public BitLockerVhdxDataVolumeOperator Operator(BitLockerVhdxOperatorOptions? options = null) =>
        new(Disks, Volumes, Mounts, Runner, options ?? FastOptions);

    public static DataVolumeDescriptor Descriptor(string volumeId = Guid) => new(
        FormatVersion: 1,
        InstallId: "install",
        VolumeId: volumeId,
        ProviderKind: BitLockerVhdxDataVolumeOperator.ProviderKind,
        ContainerKind: "file",
        ContainerPath: Container,
        Cipher: DataVolumeCiphers.XtsAes256,
        InstanceClass: InstanceClass.Production,
        KekId: "kek",
        WrappedVolumeKey: [],
        WrappedMarkerKey: [],
        CreatedAt: DateTimeOffset.UnixEpoch);

    public static DataVolumeCreateRequest Request(string container = Container, string mount = Mount) =>
        new("file", container, mount, 256L * 1024 * 1024, @"NT SERVICE\BazaarServer");

    public void Record(string call)
    {
        Calls.Add(call);
        if (string.Equals(FailOn, call, StringComparison.Ordinal))
            throw new InvalidOperationException($"{call} failed (test).");
    }
}

internal sealed class FakeDisks(FakeWorld world) : IVirtualDisks
{
    public bool Exists { get; set; }

    public bool Attached { get; set; }

    public MountFolderState Folder { get; set; } = MountFolderState.Empty;

    public bool ContainerExists(string vhdxPath) => Exists;

    public MountFolderState InspectMountFolder(string folder) => Folder;

    public ValueTask CreateAsync(string vhdxPath, long maxSizeBytes, CancellationToken cancellationToken)
    {
        world.Record("disks.create");
        Exists = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsAttachedAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        world.Record("disks.isattached");
        return ValueTask.FromResult(Attached);
    }

    public ValueTask AttachAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        world.Record("disks.attach");
        Attached = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask InitialiseAndMountAsync(string vhdxPath, string folder, CancellationToken cancellationToken)
    {
        world.Record("disks.mount");
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveMountPointAsync(string folder, CancellationToken cancellationToken)
    {
        world.Record("disks.unmount");
        return ValueTask.CompletedTask;
    }

    public ValueTask DetachAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        world.Record("disks.detach");
        Attached = false;
        return ValueTask.CompletedTask;
    }

    public void DeleteContainer(string vhdxPath)
    {
        world.Calls.Add("disks.delete");
        Exists = false;
    }
}

internal sealed class FakeBitLocker(FakeWorld world) : IBitLockerVolumes
{
    public int Locked { get; set; }

    public int Protection { get; set; } = BitLockerValues.ProtectionOn;

    public int Method { get; set; } = BitLockerValues.XtsAes256;

    /// <summary>Conversion statuses returned in turn; the last one repeats. The default is complete at once.</summary>
    public Queue<int> Conversion { get; } = new([BitLockerConversion.FullyEncrypted]);

    public uint LockResult { get; set; }

    public bool KeyIsWrong { get; set; }

    public byte[]? ProtectedKey { get; private set; }

    public byte[]? UnlockKey { get; private set; }

    public ValueTask ProtectKeyWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken)
    {
        world.Record("volumes.protect");
        ProtectedKey = externalKey.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask EncryptAsync(string volumeGuid, int encryptionMethod, int encryptionFlags, CancellationToken cancellationToken)
    {
        world.Record($"volumes.encrypt method={encryptionMethod} flags={encryptionFlags}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetConversionStatusAsync(string volumeGuid, CancellationToken cancellationToken)
    {
        world.Record("volumes.conversion");
        var value = Conversion.Count > 1 ? Conversion.Dequeue() : Conversion.Peek();
        return ValueTask.FromResult(value);
    }

    public ValueTask<int> GetProtectionStatusAsync(string volumeGuid, CancellationToken cancellationToken)
    {
        world.Record("volumes.protection");
        return ValueTask.FromResult(Protection);
    }

    public ValueTask<int> GetEncryptionMethodAsync(string volumeGuid, CancellationToken cancellationToken)
    {
        world.Record("volumes.method");
        return ValueTask.FromResult(Method);
    }

    public ValueTask<int> GetLockStatusAsync(string volumeGuid, CancellationToken cancellationToken)
    {
        world.Record("volumes.lockstatus");
        return ValueTask.FromResult(Locked);
    }

    public ValueTask UnlockWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken)
    {
        world.Record("volumes.unlock");
        UnlockKey = externalKey.ToArray();
        if (KeyIsWrong)
            throw new BitLockerException("wrong key (test)", 0x80310000);

        Locked = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask<uint> LockAsync(string volumeGuid, bool forceDismount, CancellationToken cancellationToken)
    {
        world.Record($"volumes.lock force={forceDismount}");
        if (LockResult == 0)
            Locked = 1;

        return ValueTask.FromResult(LockResult);
    }
}

internal sealed class FakeMounts(FakeWorld world) : IMountPointReader
{
    public string? Guid { get; set; } = FakeWorld.Guid;

    public string? TryGetVolumeGuid(string folder)
    {
        world.Calls.Add("mounts.read");
        return Guid;
    }
}

internal sealed class FakeRunner(FakeWorld world) : ICommandRunner
{
    public List<CommandSpec> Commands { get; } = [];

    public int ExitCode { get; set; }

    public ValueTask<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        world.Record($"runner.{command.Tool}");
        return ValueTask.FromResult(new CommandResult(ExitCode, string.Empty, string.Empty));
    }
}

/// <summary>A scripted <see cref="ICommandRunner"/> for the real seams: it records the commands and answers from a queue.</summary>
internal sealed class ScriptedRunner : ICommandRunner
{
    private readonly Queue<CommandResult> _results = new();

    public List<CommandSpec> Commands { get; } = [];

    public ScriptedRunner Answer(int exitCode, string stdout = "", string stderr = "")
    {
        _results.Enqueue(new CommandResult(exitCode, stdout, stderr));
        return this;
    }

    public ValueTask<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return ValueTask.FromResult(_results.Count > 0 ? _results.Dequeue() : new CommandResult(0, string.Empty, string.Empty));
    }
}
