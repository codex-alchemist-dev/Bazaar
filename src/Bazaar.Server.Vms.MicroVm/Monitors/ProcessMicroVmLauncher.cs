using System.Diagnostics;

namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>Launches a monitor with <see cref="Process"/>: argument list only, no shell, no inherited standard input.</summary>
public sealed class ProcessMicroVmLauncher : IMicroVmProcessLauncher
{
    public IMicroVmProcess Launch(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || !Path.IsPathRooted(argv[0]))
        {
            throw new ArgumentException("The executable must be an absolute path.", nameof(argv));
        }

        var info = new ProcessStartInfo(argv[0]) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var a in argv.Skip(1))
        {
            info.ArgumentList.Add(a);
        }

        return new Wrapper(Process.Start(info) ?? throw new InvalidOperationException($"Could not start {argv[0]}."));
    }

    private sealed class Wrapper(Process process) : IMicroVmProcess
    {
        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>Creates and deletes a tap with <c>ip tuntap</c>, through the launcher (argument list, absolute <c>ip</c> path).</summary>
public sealed class IpTapNetwork(string ipPath, IMicroVmProcessLauncher launcher) : IMicroVmTapNetwork
{
    public Task CreateAsync(string tapName, CancellationToken ct) => RunAsync(["tuntap", "add", "dev", tapName, "mode", "tap"], ct);

    public Task DeleteAsync(string tapName, CancellationToken ct) => RunAsync(["link", "delete", tapName], ct);

    private async Task RunAsync(string[] args, CancellationToken ct)
    {
        using var p = launcher.Launch([ipPath, .. args]);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"ip {args[0]} failed with exit code {p.ExitCode}.");
        }
    }
}
