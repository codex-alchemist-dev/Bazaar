namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// <see cref="IVirtualDisks"/> over <c>diskpart</c> scripts on stdin (the Hyper-V cmdlets are not on every edition) and
/// <c>Get-DiskImage</c> for the attached query. Every path is validated by <see cref="ScriptPaths"/> inside the script builders.
/// </summary>
internal sealed class DiskpartVirtualDisks(ICommandRunner runner) : IVirtualDisks
{
    public bool ContainerExists(string vhdxPath) => File.Exists(vhdxPath);

    public MountFolderState InspectMountFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return MountFolderState.Missing;

        return Directory.EnumerateFileSystemEntries(folder).Any() ? MountFolderState.NotEmpty : MountFolderState.Empty;
    }

    public async ValueTask CreateAsync(string vhdxPath, long maxSizeBytes, CancellationToken cancellationToken) =>
        await RunDiskpartAsync(DiskpartScripts.CreateVdisk(vhdxPath, maxSizeBytes), cancellationToken);

    public async ValueTask<bool> IsAttachedAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(DiskImageScripts.IsAttached(vhdxPath).ToCommand(), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("The attached state of the virtual disk could not be read.");

        return string.Equals(result.StandardOutput.Trim(), "True", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask AttachAsync(string vhdxPath, CancellationToken cancellationToken) =>
        await RunDiskpartAsync(DiskpartScripts.Attach(vhdxPath), cancellationToken);

    public async ValueTask InitialiseAndMountAsync(string vhdxPath, string folder, CancellationToken cancellationToken) =>
        await RunDiskpartAsync(DiskpartScripts.InitialiseAndMount(vhdxPath, folder), cancellationToken);

    public async ValueTask RemoveMountPointAsync(string folder, CancellationToken cancellationToken)
    {
        // mountvol wants the folder with a trailing backslash.
        var path = ScriptPaths.ValidateFolder(folder, nameof(folder)).TrimEnd('\\') + "\\";
        var result = await runner.RunAsync(new CommandSpec(SystemTools.Mountvol, [path, "/D"]), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("The mount point could not be removed.");
    }

    public async ValueTask DetachAsync(string vhdxPath, CancellationToken cancellationToken) =>
        await RunDiskpartAsync(DiskpartScripts.Detach(vhdxPath), cancellationToken);

    public void DeleteContainer(string vhdxPath)
    {
        if (File.Exists(vhdxPath))
            File.Delete(vhdxPath);
    }

    private async ValueTask RunDiskpartAsync(string script, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new CommandSpec(SystemTools.Diskpart, [], script), cancellationToken);

        // diskpart can print an error and still exit 0 for the last command, so check the text as well as the code.
        if (!result.Succeeded || result.StandardOutput.Contains("has encountered an error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"diskpart failed (exit {result.ExitCode}).");
    }
}
