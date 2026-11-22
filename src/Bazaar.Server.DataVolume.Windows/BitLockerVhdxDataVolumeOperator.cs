using System.Globalization;
using System.Runtime.Versioning;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>Timing of the operator. The defaults suit a used-space-only encryption, which finishes in seconds (spike D1).</summary>
public sealed record BitLockerVhdxOperatorOptions
{
    /// <summary>How often the conversion status is polled.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait for the encryption to finish before create fails and rolls back.</summary>
    public TimeSpan ConversionTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often <c>open</c> looks for the folder mount to return after an attach.</summary>
    public int MountAttempts { get; init; } = 20;
}

/// <summary>
/// The privileged half of the <c>bitlocker-vhdx</c> provider (doc 63 sections 3.1 and 4): a VHDX file, attached, GPT and NTFS,
/// mounted on a folder, encrypted with XTS-AES-256 under one external-key protector holding the volume key. Every call needs
/// an administrator token; only the volume unit and setup use it.
/// </summary>
/// <remarks>
/// The volume id it returns and compares is the canonical GUID (<see cref="VolumeGuid"/>): 36 lowercase characters, no braces.
/// The key is passed to the BitLocker seam and nowhere else, and is never written to a file or a log. There is no force
/// option: <c>close</c> locks with ForceDismount false, because a forced lock under a running PostgreSQL kills it.
/// </remarks>
public sealed class BitLockerVhdxDataVolumeOperator : IDataVolumeOperator
{
    public const string ProviderKind = "bitlocker-vhdx";
    public const string FileContainerKind = "file";

    private readonly IVirtualDisks _disks;
    private readonly IBitLockerVolumes _volumes;
    private readonly IMountPointReader _mounts;
    private readonly ICommandRunner _runner;
    private readonly BitLockerVhdxOperatorOptions _options;

    internal BitLockerVhdxDataVolumeOperator(
        IVirtualDisks disks,
        IBitLockerVolumes volumes,
        IMountPointReader mounts,
        ICommandRunner runner,
        BitLockerVhdxOperatorOptions? options = null)
    {
        _disks = disks;
        _volumes = volumes;
        _mounts = mounts;
        _runner = runner;
        _options = options ?? new BitLockerVhdxOperatorOptions();
    }

    public string Kind => ProviderKind;

    /// <summary>The operator with the real Windows seams, for the volume unit and setup.</summary>
    [SupportedOSPlatform("windows")]
    public static BitLockerVhdxDataVolumeOperator CreateDefault(BitLockerVhdxOperatorOptions? options = null)
    {
        var runner = new ProcessCommandRunner();
        return new BitLockerVhdxDataVolumeOperator(
            new DiskpartVirtualDisks(runner), new PowerShellBitLockerVolumes(runner), new NativeMountPointReader(), runner, options);
    }

    public async ValueTask<string> CreateAsync(DataVolumeCreateRequest request, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (volumeKey.Length != 32)
            throw new ArgumentException("The volume key must be 32 bytes.", nameof(volumeKey));

        if (!string.Equals(request.ContainerKind, FileContainerKind, StringComparison.Ordinal))
            throw new ArgumentException("The bitlocker-vhdx provider supports only a file container.", nameof(request));

        var container = ScriptPaths.Validate(request.ContainerPath, nameof(request), requireVhdx: true);
        var mount = ScriptPaths.ValidateFolder(request.MountPath, nameof(request));
        var account = ValidateAccount(request.ServiceAccount);
        if (request.MaxSizeBytes < DiskpartScripts.MinimumSizeBytes)
            throw new ArgumentException("The container size is below the minimum.", nameof(request));

        // Refuse before any command runs: an existing file or a used folder is never touched.
        if (_disks.ContainerExists(container))
            throw new IOException($"The container '{container}' already exists. Create never overwrites a volume.");

        switch (_disks.InspectMountFolder(mount))
        {
            case MountFolderState.Missing:
                throw new DirectoryNotFoundException($"The mount folder '{mount}' does not exist.");
            case MountFolderState.NotEmpty:
                throw new IOException($"The mount folder '{mount}' is not empty.");
        }

        // Each flag is set before the call it covers, so a call that fails half way is still undone.
        bool fileCreated = false, attached = false, mounted = false;
        try
        {
            fileCreated = true;
            await _disks.CreateAsync(container, request.MaxSizeBytes, cancellationToken);
            attached = true;
            await _disks.AttachAsync(container, cancellationToken);
            mounted = true;
            await _disks.InitialiseAndMountAsync(container, mount, cancellationToken);

            var guid = _mounts.TryGetVolumeGuid(mount)
                ?? throw new InvalidOperationException("The new volume is not mounted on the folder.");

            await _volumes.ProtectKeyWithExternalKeyAsync(guid, volumeKey, cancellationToken);
            await _volumes.EncryptAsync(guid, BitLockerValues.XtsAes256, BitLockerValues.UsedSpaceOnly, cancellationToken);
            await WaitForConversionAsync(guid, cancellationToken);

            if (await _volumes.GetProtectionStatusAsync(guid, cancellationToken) != BitLockerValues.ProtectionOn)
                throw new InvalidOperationException("BitLocker protection is not on after encryption.");

            if (await _volumes.GetEncryptionMethodAsync(guid, cancellationToken) != BitLockerValues.XtsAes256)
                throw new InvalidOperationException("The volume is not encrypted with XTS-AES-256.");

            await GrantServiceAccountAsync(mount, account, cancellationToken);
            return guid;
        }
        catch
        {
            await RollbackAsync(container, mount, fileCreated, attached, mounted);
            throw;
        }
    }

    public async ValueTask OpenAsync(DataVolumeDescriptor descriptor, string mountPath, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (volumeKey.Length != 32)
            throw new ArgumentException("The volume key must be 32 bytes.", nameof(volumeKey));

        RequireOwnKind(descriptor);
        var container = ScriptPaths.Validate(descriptor.ContainerPath, nameof(descriptor), requireVhdx: true);
        var mount = ScriptPaths.ValidateFolder(mountPath, nameof(mountPath));
        var expected = ExpectedGuid(descriptor);

        var attachedHere = false;
        try
        {
            if (!await _disks.IsAttachedAsync(container, cancellationToken))
            {
                await _disks.AttachAsync(container, cancellationToken);
                attachedHere = true;
            }

            var actual = await WaitForMountAsync(mount, cancellationToken);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("The volume mounted at the data root is not the volume the descriptor names.");

            if (await _volumes.GetLockStatusAsync(actual, cancellationToken) == BitLockerValues.Locked)
                await _volumes.UnlockWithExternalKeyAsync(actual, volumeKey, cancellationToken);

            if (await _volumes.GetProtectionStatusAsync(actual, cancellationToken) != BitLockerValues.ProtectionOn)
                throw new InvalidOperationException("BitLocker protection is not on for the opened volume.");
        }
        catch
        {
            // Leave a disk that was already attached before this call as it was; undo only what this call attached.
            if (attachedHere)
                await TryAsync(() => _disks.DetachAsync(container, CancellationToken.None));

            throw;
        }
    }

    public async ValueTask CloseAsync(DataVolumeDescriptor descriptor, string mountPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        RequireOwnKind(descriptor);
        var container = ScriptPaths.Validate(descriptor.ContainerPath, nameof(descriptor), requireVhdx: true);
        var mount = ScriptPaths.ValidateFolder(mountPath, nameof(mountPath));
        var expected = ExpectedGuid(descriptor);

        if (!await _disks.IsAttachedAsync(container, cancellationToken))
            return;

        var actual = _mounts.TryGetVolumeGuid(mount);
        if (actual is null || !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The volume mounted at the data root is not the volume the descriptor names. Nothing was closed.");

        if (await _volumes.GetLockStatusAsync(actual, cancellationToken) != BitLockerValues.Locked)
        {
            // Never force: a forced lock under a running PostgreSQL kills it (doc 63 section 3.1).
            var result = await _volumes.LockAsync(actual, forceDismount: false, cancellationToken);
            if (result != 0)
                throw new InvalidOperationException(
                    $"The volume could not be locked (0x{result:X8}); files are probably still open under the data root. It was not detached.");
        }

        await _disks.DetachAsync(container, cancellationToken);
    }

    private static string ExpectedGuid(DataVolumeDescriptor descriptor) =>
        VolumeGuid.TryCanonicalize(descriptor.VolumeId, out var guid)
            ? guid
            : throw new ArgumentException("The descriptor's volume id is not a volume GUID.", nameof(descriptor));

    private void RequireOwnKind(DataVolumeDescriptor descriptor)
    {
        if (!string.Equals(descriptor.ProviderKind, Kind, StringComparison.Ordinal))
            throw new ArgumentException($"The descriptor is for provider '{descriptor.ProviderKind}', not '{Kind}'.", nameof(descriptor));
    }

    private static string ValidateAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 256 || account.Any(c => char.IsControl(c) || c is '"' or ':' or '/' or '*' or '?'))
            throw new ArgumentException("The service account name is not valid.", nameof(account));

        return account;
    }

    /// <summary>After an attach the folder mount returns by itself, but not instantly; look a bounded number of times.</summary>
    private async ValueTask<string> WaitForMountAsync(string mount, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MountAttempts; attempt++)
        {
            var guid = _mounts.TryGetVolumeGuid(mount);
            if (guid is not null)
                return guid;

            await Task.Delay(_options.PollInterval, cancellationToken);
        }

        throw new InvalidOperationException("The volume did not come back on the mount folder after attach.");
    }

    private async ValueTask WaitForConversionAsync(string guid, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.ConversionTimeout);
        try
        {
            while (await _volumes.GetConversionStatusAsync(guid, limit.Token) != BitLockerConversion.FullyEncrypted)
                await Task.Delay(_options.PollInterval, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"The encryption did not finish within {_options.ConversionTimeout}."));
        }
    }

    /// <summary>Gives the unprivileged service account Modify on the volume root, inherited by everything below it.</summary>
    private async ValueTask GrantServiceAccountAsync(string mount, string account, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new CommandSpec(SystemTools.Icacls, [mount.TrimEnd('\\') + "\\", "/grant", $"{account}:(OI)(CI)M"]), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("Granting the service account access to the data volume failed.");
    }

    /// <summary>Undoes what create made. Every step is best effort and the original failure is what the caller sees.</summary>
    private async ValueTask RollbackAsync(string container, string mount, bool fileCreated, bool attached, bool mounted)
    {
        if (mounted)
            await TryAsync(() => _disks.RemoveMountPointAsync(mount, CancellationToken.None));

        if (attached)
            await TryAsync(() => _disks.DetachAsync(container, CancellationToken.None));

        if (fileCreated)
            await TryAsync(() =>
            {
                _disks.DeleteContainer(container);
                return ValueTask.CompletedTask;
            });
    }

    private static async ValueTask TryAsync(Func<ValueTask> step)
    {
        try
        {
            await step();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Rollback is best effort; the caller already has the real error.
        }
    }
}
