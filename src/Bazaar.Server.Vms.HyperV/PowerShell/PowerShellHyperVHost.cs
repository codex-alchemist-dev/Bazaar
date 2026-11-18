using System.Globalization;
using System.Text;

namespace Bazaar.Server.Vms.HyperV.PowerShell;

/// <summary>
/// The real <see cref="IHyperVHost"/>, driven by <see cref="HyperVScript"/> through <c>powershell.exe -File</c>. The script is written to
/// <paramref name="scriptDirectory"/> under a name that carries its hash and is rewritten if its content differs, so it cannot drift
/// or be swapped. Every operation is a fixed argument list: names are checked against <see cref="HyperVNames"/> here and again in the script.
/// Child disks live under <paramref name="vmDirectory"/>, one folder per VM.
/// </summary>
public sealed class PowerShellHyperVHost(IHyperVCommandRunner runner, string scriptDirectory, string vmDirectory) : IHyperVHost, IDisposable
{
    private static readonly UTF8Encoding _utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <inheritdoc />
    public async Task<string> CreateDifferencingDiskAsync(string vmName, string parentDiskPath, CancellationToken ct)
    {
        RequireVmName(vmName);
        if (!Path.IsPathFullyQualified(parentDiskPath) || !parentDiskPath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The parent disk must be a full path to a .vhdx file.", nameof(parentDiskPath));
        }

        var folder = Path.Combine(vmDirectory, vmName);
        Directory.CreateDirectory(folder);
        var diskPath = Path.Combine(folder, "disk.vhdx");

        await RunAsync(["-Operation", "CreateDisk", "-Name", vmName, "-ParentPath", parentDiskPath, "-DiskPath", diskPath], "create disk", ct).ConfigureAwait(false);
        return diskPath;
    }

    /// <inheritdoc />
    public async Task CreateVmAsync(HyperVVmDefinition vm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vm);
        RequireVmName(vm.Name);

        await RunAsync(
            [
                "-Operation", "CreateVm",
                "-Name", vm.Name,
                "-DiskPath", vm.DiskPath,
                "-GuestOs", vm.Os.ToString(),
                "-Vcpu", vm.Vcpu.ToString(CultureInfo.InvariantCulture),
                "-MemoryMb", vm.MemoryMb.ToString(CultureInfo.InvariantCulture),
            ],
            "create VM",
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StartAsync(string vmName, CancellationToken ct) => Simple("Start", vmName, ct);

    /// <inheritdoc />
    public Task StopAsync(string vmName, bool force, CancellationToken ct)
    {
        RequireVmName(vmName);
        IReadOnlyList<string> args = force
            ? ["-Operation", "Stop", "-Name", vmName, "-Force"]
            : ["-Operation", "Stop", "-Name", vmName];
        return RunAsync(args, "stop", ct);
    }

    /// <inheritdoc />
    public Task SaveAsync(string vmName, CancellationToken ct) => Simple("Save", vmName, ct);

    /// <inheritdoc />
    public Task CheckpointAsync(string vmName, string checkpointName, CancellationToken ct) => WithCheckpoint("Checkpoint", vmName, checkpointName, ct);

    /// <inheritdoc />
    public Task RestoreAsync(string vmName, string checkpointName, CancellationToken ct) => WithCheckpoint("Restore", vmName, checkpointName, ct);

    /// <inheritdoc />
    public async Task RemoveAsync(string vmName, bool deleteDisk, CancellationToken ct)
    {
        RequireVmName(vmName);
        var folder = Path.Combine(vmDirectory, vmName);
        var args = new List<string> { "-Operation", "Remove", "-Name", vmName, "-DiskPath", Path.Combine(folder, "disk.vhdx") };
        if (deleteDisk)
        {
            args.Add("-DeleteDisk");
        }

        await RunAsync(args, "remove", ct).ConfigureAwait(false);
        if (deleteDisk && Directory.Exists(folder))
        {
            // The script removes the disks; the empty per-VM folder goes with them.
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
    }

    /// <inheritdoc />
    public async Task<HyperVVmState> GetStateAsync(string vmName, CancellationToken ct)
    {
        RequireVmName(vmName);
        var result = await RunRawAsync(["-Operation", "State", "-Name", vmName], ct).ConfigureAwait(false);
        EnsureSuccess(result, "read state");

        return result.StandardOutput.Trim() switch
        {
            "Missing" => HyperVVmState.Missing,
            "Off" => HyperVVmState.Off,
            "Running" => HyperVVmState.Running,
            "Paused" => HyperVVmState.Paused,
            "Saved" => HyperVVmState.Saved,
            var other when other.Length > 0 => HyperVVmState.Transitioning,
            _ => throw new HyperVCommandException("Hyper-V returned no state."),
        };
    }

    /// <inheritdoc />
    public void Dispose() => _installGate.Dispose();

    private Task Simple(string operation, string vmName, CancellationToken ct)
    {
        RequireVmName(vmName);
        return RunAsync(["-Operation", operation, "-Name", vmName], operation.ToLowerInvariant(), ct);
    }

    private Task WithCheckpoint(string operation, string vmName, string checkpointName, CancellationToken ct)
    {
        RequireVmName(vmName);
        if (!HyperVNames.IsCheckpointName(checkpointName))
        {
            throw new ArgumentException("The checkpoint name is not one this provider creates.", nameof(checkpointName));
        }

        return RunAsync(["-Operation", operation, "-Name", vmName, "-CheckpointName", checkpointName], operation.ToLowerInvariant(), ct);
    }

    private async Task RunAsync(IReadOnlyList<string> scriptArguments, string what, CancellationToken ct)
    {
        var result = await RunRawAsync(scriptArguments, ct).ConfigureAwait(false);
        EnsureSuccess(result, what);
    }

    private async Task<HyperVCommandResult> RunRawAsync(IReadOnlyList<string> scriptArguments, CancellationToken ct)
    {
        var script = await EnsureScriptAsync(ct).ConfigureAwait(false);
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", script,
        };
        arguments.AddRange(scriptArguments);
        return await runner.RunAsync(arguments, ct).ConfigureAwait(false);
    }

    private async Task<string> EnsureScriptAsync(CancellationToken ct)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(scriptDirectory);
            var path = Path.Combine(scriptDirectory, $"bazaar-hyperv-{HyperVScript.Hash()}.ps1");

            // Checked on every call: a script file that was changed on disk is replaced before it runs.
            if (!File.Exists(path) || await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) != HyperVScript.Text)
            {
                await File.WriteAllTextAsync(path, HyperVScript.Text, _utf8Bom, ct).ConfigureAwait(false);
            }

            return path;
        }
        finally
        {
            _installGate.Release();
        }
    }

    private static void EnsureSuccess(HyperVCommandResult result, string what)
    {
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
            throw new HyperVCommandException($"Hyper-V could not {what} (exit {result.ExitCode}): {detail}");
        }
    }

    private static void RequireVmName(string vmName)
    {
        if (!HyperVNames.IsVmName(vmName))
        {
            throw new ArgumentException("The VM name is not one this provider creates.", nameof(vmName));
        }
    }
}
