using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>Cleanup shared by every failure point and by dispose. Every step is attempted; none throws.</summary>
internal static class WindowsCleanup
{
    internal static void Run(IWindowsConfinementApi api, IWindowsJob? job, IWindowsProcess? process, string? profileName, string? workDirectory)
    {
        Try(() => process?.Terminate(1));
        Try(() => job?.Terminate(1));
        Try(() => process?.Dispose());
        Try(() => job?.Dispose());
        if (profileName is not null)
        {
            Try(() => api.DeleteAppContainerProfile(profileName));
        }

        if (workDirectory is not null)
        {
            Try(() => DeleteDirectory(workDirectory));
        }
    }

    private static void Try(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cleanup is best effort and must reach every step.
        }
    }

    private static void DeleteDirectory(string path)
    {
        // A just-terminated process can hold a file handle for a moment.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 20)
                {
                    throw;
                }

                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>
/// The launch sequence shared by the provider and the self-test, so the probes exercise exactly the production path. The order is:
/// job object, AppContainer profile, work directory, work directory access list, command, process created suspended, assigned to the
/// job, resumed. A process never runs before it is in the job. On any failure everything created so far is torn down.
/// </summary>
internal static partial class WindowsLauncher
{
    private const int _maxProfileRunPart = 32;

    internal static WindowsSandboxProcess Launch(
        IWindowsConfinementApi api,
        WindowsConfinementOptions options,
        string label,
        WindowsJobSpec jobSpec,
        WindowsConfinementMode mode,
        Func<string, WindowsLaunchCommand> commandFactory,
        bool allowShell)
    {
        IWindowsJob? job = null;
        IWindowsProcess? process = null;
        string? profileName = null;
        string? workDirectory = null;
        try
        {
            job = api.CreateJob(jobSpec);

            string? sid = null;
            if (mode == WindowsConfinementMode.AppContainer)
            {
                var profile = api.CreateAppContainerProfile(BuildProfileName(label));
                profileName = profile.Name;
                sid = profile.Sid;
            }

            workDirectory = Path.Combine(options.ResolvedWorkRoot, "bazaar-sbx-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            api.GrantWorkDirectoryAccess(workDirectory, sid);

            var command = commandFactory(workDirectory);
            ValidateExecutable(api, command.Executable, allowShell);
            var request = new WindowsProcessRequest(
                command.Executable,
                WindowsCommandLine.Build(command.Executable, command.Arguments),
                workDirectory,
                WindowsEnvironmentBlock.ForRun(options.ResolvedSystemRoot, workDirectory),
                mode,
                sid);

            process = api.CreateProcessSuspended(request);
            job.Assign(process);
            process.Resume();
            return new WindowsSandboxProcess(api, job, process, profileName, workDirectory);
        }
        catch
        {
            WindowsCleanup.Run(api, job, process, profileName, workDirectory);
            throw;
        }
    }

    /// <summary>A profile name unique per launch: at most 64 characters of letters, digits, dot and hyphen.</summary>
    internal static string BuildProfileName(string label)
    {
        var part = label.Length > _maxProfileRunPart ? label[.._maxProfileRunPart] : label;
        return "bazaar.sbx." + part + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    }

    private static void ValidateExecutable(IWindowsConfinementApi api, string executable, bool allowShell)
    {
        if (!DriveLetterPath().IsMatch(executable))
        {
            throw new InvalidOperationException("The launch plan must give an absolute path with a drive letter (for example C:\\...).");
        }

        if (executable.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The launch plan's executable path must not contain '..'.");
        }

        if (!allowShell)
        {
            var name = executable[(executable.LastIndexOf('\\') + 1)..];
            if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Batch files and cmd.exe are refused: a shell reparses the arguments.");
            }
        }

        if (!api.FileExists(executable))
        {
            throw new InvalidOperationException("The launch plan's executable does not exist.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z]:\\[^\0""<>|*?]+$")]
    private static partial Regex DriveLetterPath();

    /// <summary>The job limits for a run spec: memory from <c>MemoryLimitMb</c> (per process and per job), CPU time from <c>TimeoutSeconds</c>.</summary>
    internal static WindowsJobSpec JobSpecFor(SandboxRunSpec spec, WindowsConfinementOptions options)
    {
        var bytes = checked((long)spec.MemoryLimitMb * 1024 * 1024);
        return new WindowsJobSpec(bytes, bytes, TimeSpan.FromSeconds(spec.TimeoutSeconds), options.MaxProcesses);
    }
}
