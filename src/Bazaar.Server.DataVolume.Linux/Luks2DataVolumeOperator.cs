using System.Globalization;
using System.Text;

namespace Bazaar.Server.DataVolume.Linux;

/// <summary>
/// The privileged half of the Linux <c>luks2</c> provider (doc 63 sections 3, 4 and 7). It runs as root inside the volume
/// unit and shells out to <c>cryptsetup</c>, <c>losetup</c>, <c>mount</c>, <c>umount</c>, <c>mkfs.ext4</c> and <c>truncate</c>
/// through <see cref="ICommandRunner"/>. The volume key reaches cryptsetup only on stdin; it is never in an argument, the
/// environment or a file. Every path and name is validated, and a failed step undoes what this call created.
/// </summary>
public sealed class Luks2DataVolumeOperator : IDataVolumeOperator
{
    private const int _keyLength = 32;
    private const long _minContainerBytes = 64L * 1024 * 1024;
    private const string _mapperDirectory = "/dev/mapper/";
    private const string _mountInfoPath = "/proc/self/mountinfo";

    // The container file is readable by the service account's group, so the unprivileged host can read the LUKS2 header (cipher check).
    private const UnixFileMode _containerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private readonly ICommandRunner _runner;
    private readonly ILinuxVolumeFiles _files;

    public Luks2DataVolumeOperator()
        : this(new ProcessCommandRunner(), new SystemLinuxVolumeFiles())
    {
    }

    internal Luks2DataVolumeOperator(ICommandRunner runner, ILinuxVolumeFiles files)
    {
        _runner = runner;
        _files = files;
    }

    public string Kind => Luks2DataVolumeProvider.ProviderKind;

    public async ValueTask<string> CreateAsync(DataVolumeCreateRequest request, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKey(volumeKey);
        var containerPath = LinuxPaths.Require(request.ContainerPath, nameof(request.ContainerPath));
        var mountPath = LinuxPaths.Require(request.MountPath, nameof(request.MountPath));
        var account = LinuxPaths.RequireAccount(request.ServiceAccount);
        var isFile = request.ContainerKind switch
        {
            "file" => true,
            "device" => false,
            _ => throw new ArgumentException("The container kind must be 'file' or 'device'.", nameof(request)),
        };
        if (isFile && request.MaxSizeBytes < _minContainerBytes)
            throw new ArgumentException("The container must be at least 64 MiB.", nameof(request));

        // Everything that can be checked without touching anything comes first.
        if (!_files.DirectoryExists(mountPath) || !_files.IsDirectoryEmpty(mountPath))
            throw new InvalidOperationException("The mount path must be an existing empty folder.");
        if (IsMountPoint(mountPath))
            throw new InvalidOperationException("The mount path is already a mount point.");
        if (isFile && (_files.FileExists(containerPath) || _files.DirectoryExists(containerPath)))
            throw new InvalidOperationException("The container file already exists; it is never overwritten.");
        if (!isFile)
            await RequireBlankDeviceAsync(containerPath, cancellationToken).ConfigureAwait(false);

        var undo = new List<Func<Task>>();
        try
        {
            var device = containerPath;
            if (isFile)
            {
                _files.CreateExclusiveFile(containerPath, _containerMode);
                undo.Add(() => { _files.DeleteFile(containerPath); return Task.CompletedTask; });
                await RunOkAsync("truncate", ["--size=" + request.MaxSizeBytes.ToString(CultureInfo.InvariantCulture), containerPath], default, cancellationToken).ConfigureAwait(false);

                // The group is the service account's own group; it only reads the header.
                await RunOkAsync("chgrp", [account, containerPath], default, cancellationToken).ConfigureAwait(false);
                device = await AttachLoopAsync(containerPath, cancellationToken).ConfigureAwait(false);
                var loop = device;
                undo.Add(() => RunQuietAsync("losetup", ["--detach", loop]));
            }
            else
            {
                undo.Add(() => RunQuietAsync("wipefs", ["--all", containerPath]));
            }

            // The key is 32 random bytes, so a slow key derivation adds nothing: one pbkdf2 iteration round (the minimum, 1000)
            // keeps the format step fast. Argon2id is not used for the same reason (doc 63 section 3).
            await RunOkAsync(
                "cryptsetup",
                [
                    "luksFormat", "--type", "luks2", "--cipher", "aes-xts-plain64", "--key-size", "512",
                    "--pbkdf", "pbkdf2", "--pbkdf-force-iterations", "1000", "--batch-mode",
                    "--key-file", "-", "--keyfile-size", "32", device,
                ],
                volumeKey,
                cancellationToken).ConfigureAwait(false);

            var uuid = await ReadUuidAsync(device, cancellationToken).ConfigureAwait(false);
            var name = Luks2DataVolumeProvider.MappingName(uuid);
            await RunOkAsync("cryptsetup", OpenArguments(device, name), volumeKey, cancellationToken).ConfigureAwait(false);
            undo.Add(() => RunQuietAsync("cryptsetup", ["close", name]));

            await RunOkAsync("mkfs.ext4", ["-q", "-L", "bazaar-data", _mapperDirectory + name], default, cancellationToken).ConfigureAwait(false);
            await RunOkAsync("mount", MountArguments(name, mountPath), default, cancellationToken).ConfigureAwait(false);
            undo.Add(() => RunQuietAsync("umount", [mountPath]));

            await RunOkAsync("chown", [account, mountPath], default, cancellationToken).ConfigureAwait(false);
            await RunOkAsync("chmod", ["0700", mountPath], default, cancellationToken).ConfigureAwait(false);
            return uuid;
        }
        catch
        {
            await UndoAsync(undo).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask OpenAsync(DataVolumeDescriptor descriptor, string mountPath, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
    {
        var (name, containerPath, isFile) = ValidateDescriptor(descriptor);
        RequireKey(volumeKey);
        LinuxPaths.Require(mountPath, nameof(mountPath));

        var mounted = IsMountPoint(mountPath);
        var active = await IsMappingActiveAsync(name, cancellationToken).ConfigureAwait(false);
        if (active && mounted)
            return;
        if (mounted)
            throw new InvalidOperationException("Something other than the data volume is mounted at the mount path.");
        if (!_files.DirectoryExists(mountPath))
            throw new InvalidOperationException("The mount path does not exist.");

        string? attachedLoop = null;
        var openedMapping = false;
        try
        {
            if (!active)
            {
                var device = containerPath;
                if (isFile)
                {
                    device = await FindLoopAsync(containerPath, cancellationToken).ConfigureAwait(false);
                    if (device is null)
                    {
                        device = await AttachLoopAsync(containerPath, cancellationToken).ConfigureAwait(false);
                        attachedLoop = device;
                    }
                }

                // Never unlock a container that is not the one the descriptor names.
                var uuid = await ReadUuidAsync(device, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(uuid, Guid.Parse(descriptor.VolumeId).ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    throw new InvalidOperationException("The container's LUKS UUID does not match the descriptor.");

                await RunOkAsync("cryptsetup", OpenArguments(device, name), volumeKey, cancellationToken).ConfigureAwait(false);
                openedMapping = true;
            }

            await RunOkAsync("mount", MountArguments(name, mountPath), default, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var undo = new List<Func<Task>>();
            if (attachedLoop is not null)
            {
                var loop = attachedLoop;
                undo.Add(() => RunQuietAsync("losetup", ["--detach", loop]));
            }

            if (openedMapping)
                undo.Add(() => RunQuietAsync("cryptsetup", ["close", name]));
            await UndoAsync(undo).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask CloseAsync(DataVolumeDescriptor descriptor, string mountPath, CancellationToken cancellationToken)
    {
        var (name, containerPath, isFile) = ValidateDescriptor(descriptor);
        LinuxPaths.Require(mountPath, nameof(mountPath));

        if (IsMountPoint(mountPath))
        {
            // No lazy and no forced unmount: a busy mount is the refusal (doc 63 section 4).
            var result = await _runner.RunAsync("umount", [mountPath], default, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var busy = result.StandardError.Contains("busy", StringComparison.OrdinalIgnoreCase);
                throw new InvalidOperationException(busy
                    ? "The data volume is in use: a process still has files open under the mount path."
                    : $"umount failed with exit code {result.ExitCode}.");
            }
        }

        if (await IsMappingActiveAsync(name, cancellationToken).ConfigureAwait(false))
            await RunOkAsync("cryptsetup", ["close", name], default, cancellationToken).ConfigureAwait(false);

        if (isFile)
        {
            var loops = await ListLoopsAsync(containerPath, cancellationToken).ConfigureAwait(false);
            foreach (var loop in loops)
                await RunOkAsync("losetup", ["--detach", loop], default, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Grows a <c>file</c> container online (doc 63 section 7): extends the sparse file, tells the loop device, resizes the
    /// mapping, then the file system. A <c>device</c> container is fixed by the operator and is refused.
    /// </summary>
    public async ValueTask GrowAsync(DataVolumeDescriptor descriptor, string mountPath, long newSizeBytes, CancellationToken cancellationToken)
    {
        var (name, containerPath, isFile) = ValidateDescriptor(descriptor);
        LinuxPaths.Require(mountPath, nameof(mountPath));
        if (!isFile)
            throw new InvalidOperationException("Only a file container can grow; a device container is fixed by the operator.");
        if (!IsMountPoint(mountPath) || !await IsMappingActiveAsync(name, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The volume is not open.");
        if (newSizeBytes <= _files.FileLength(containerPath))
            throw new ArgumentException("The new size must be larger than the current size.", nameof(newSizeBytes));

        var loop = await FindLoopAsync(containerPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The container file is not attached to a loop device.");
        await RunOkAsync("truncate", ["--size=" + newSizeBytes.ToString(CultureInfo.InvariantCulture), containerPath], default, cancellationToken).ConfigureAwait(false);
        await RunOkAsync("losetup", ["--set-capacity", loop], default, cancellationToken).ConfigureAwait(false);
        await RunOkAsync("cryptsetup", ["resize", name], default, cancellationToken).ConfigureAwait(false);
        await RunOkAsync("resize2fs", [_mapperDirectory + name], default, cancellationToken).ConfigureAwait(false);
    }

    private static (string Name, string ContainerPath, bool IsFile) ValidateDescriptor(DataVolumeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ProviderKind != Luks2DataVolumeProvider.ProviderKind)
            throw new ArgumentException("The descriptor is not a luks2 volume.", nameof(descriptor));
        var isFile = descriptor.ContainerKind switch
        {
            "file" => true,
            "device" => false,
            _ => throw new ArgumentException("The container kind must be 'file' or 'device'.", nameof(descriptor)),
        };
        return (Luks2DataVolumeProvider.MappingName(descriptor.VolumeId), LinuxPaths.Require(descriptor.ContainerPath, nameof(descriptor)), isFile);
    }

    private static void RequireKey(ReadOnlyMemory<byte> volumeKey)
    {
        if (volumeKey.Length != _keyLength)
            throw new ArgumentException("The volume key must be 32 bytes.", nameof(volumeKey));
    }

    private static string[] OpenArguments(string device, string name) =>
        ["open", "--type", "luks2", "--batch-mode", "--key-file", "-", "--keyfile-size", "32", device, name];

    private static string[] MountArguments(string name, string mountPath) =>
        ["-t", "ext4", "-o", "nodev,nosuid,noexec", _mapperDirectory + name, mountPath];

    private bool IsMountPoint(string path) =>
        _files.ReadAllText(_mountInfoPath) is { } text && MountInfoParser.FindMountAt(text, path) is not null;

    private async ValueTask RequireBlankDeviceAsync(string device, CancellationToken cancellationToken)
    {
        // blkid -p exits 0 when it found a signature, 2 when it found none.
        var result = await _runner.RunAsync("blkid", ["-p", device], default, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
            throw new InvalidOperationException("The device already has a LUKS or file system signature; it is never overwritten.");
        if (result.ExitCode != 2)
            throw new InvalidOperationException($"blkid failed with exit code {result.ExitCode}.");
    }

    private async ValueTask<bool> IsMappingActiveAsync(string name, CancellationToken cancellationToken) =>
        (await _runner.RunAsync("cryptsetup", ["status", name], default, cancellationToken).ConfigureAwait(false)).ExitCode == 0;

    private async ValueTask<string> ReadUuidAsync(string device, CancellationToken cancellationToken)
    {
        var text = (await RunOkAsync("cryptsetup", ["luksUUID", device], default, cancellationToken).ConfigureAwait(false)).Trim();
        if (!Guid.TryParse(text, out var guid))
            throw new InvalidOperationException("cryptsetup did not report a valid LUKS UUID.");
        return guid.ToString("D", CultureInfo.InvariantCulture);
    }

    private async ValueTask<string> AttachLoopAsync(string file, CancellationToken cancellationToken)
    {
        var loop = (await RunOkAsync("losetup", ["--find", "--show", file], default, cancellationToken).ConfigureAwait(false)).Trim();
        if (!LosetupOutput.IsLoopDevice(loop))
            throw new InvalidOperationException("losetup did not report a loop device.");
        return loop;
    }

    private async ValueTask<string?> FindLoopAsync(string file, CancellationToken cancellationToken) =>
        (await ListLoopsAsync(file, cancellationToken).ConfigureAwait(false)) is { Count: > 0 } loops ? loops[0] : null;

    private async ValueTask<IReadOnlyList<string>> ListLoopsAsync(string file, CancellationToken cancellationToken) =>
        LosetupOutput.ParseDevices(await RunOkAsync("losetup", ["--associated", file], default, cancellationToken).ConfigureAwait(false));

    private async ValueTask<string> RunOkAsync(string fileName, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> standardInput, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(fileName, arguments, standardInput, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // The message names the program and its stderr (cryptsetup never prints the key); arguments are not repeated.
            var detail = result.StandardError.Trim();
            if (detail.Length > 300)
                detail = detail[..300];
            throw new InvalidOperationException(new StringBuilder().Append(fileName).Append(" failed with exit code ")
                .Append(result.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(detail).ToString());
        }

        return result.StandardOutput;
    }

    /// <summary>A cleanup step: it must not mask the failure that caused it, and it ignores cancellation.</summary>
    private async Task RunQuietAsync(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            await _runner.RunAsync(fileName, arguments, default, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Best effort; the original failure is what the caller sees.
        }
    }

    private static async Task UndoAsync(List<Func<Task>> undo)
    {
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                await undo[i]().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Best effort, as above.
            }
        }
    }
}
