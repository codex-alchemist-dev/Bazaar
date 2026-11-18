using System.Globalization;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>Where the hardening helper and its policy file are, for a hardened launch.</summary>
/// <param name="HelperPath">The helper executable.</param>
/// <param name="PolicyFile">The JSON policy the helper reads and deletes before it applies any restriction.</param>
/// <param name="ExtraHelperArguments">Further helper options (self-test only), placed before the <c>--</c> separator.</param>
internal sealed record LinuxHardeningLaunch(string HelperPath, string PolicyFile, IReadOnlyList<string>? ExtraHelperArguments = null);

/// <summary>Builds the <c>prlimit ... -- unshare ... -- [helper ... --] command</c> argument vector. Pure; no shell is involved.</summary>
internal static class LinuxCommandLine
{
    internal static readonly string[] UnshareFlags =
    [
        "--user", "--map-root-user", "--net", "--pid", "--mount", "--ipc", "--uts", "--fork", "--kill-child", "--mount-proc",
    ];

    /// <summary>
    /// Builds the vector for the <c>prlimit</c> executable. Without <paramref name="hardening"/> the command runs directly under
    /// <c>unshare</c>. With it the helper runs first, and the address-space limit moves into the helper's policy: the .NET runtime of the
    /// helper reserves far more address space than a module's memory limit, so <c>prlimit --as</c> would stop the helper itself, and the
    /// helper sets the same limit just before it execs the target.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        LinuxConfinementOptions options,
        string unsharePath,
        int memoryLimitMb,
        int cpuSeconds,
        LinuxLaunchCommand command,
        LinuxHardeningLaunch? hardening = null)
    {
        var args = new List<string>();
        if (hardening is null)
        {
            args.Add("--as=" + checked((long)memoryLimitMb * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        }

        args.Add("--cpu=" + cpuSeconds.ToString(CultureInfo.InvariantCulture));
        args.Add("--nproc=" + options.MaxProcesses.ToString(CultureInfo.InvariantCulture));
        args.Add("--nofile=" + options.MaxOpenFiles.ToString(CultureInfo.InvariantCulture));
        args.Add("--");
        args.Add(unsharePath);
        args.AddRange(UnshareFlags);
        args.Add("--");
        if (hardening is not null)
        {
            args.Add(hardening.HelperPath);
            args.Add("--policy");
            args.Add(hardening.PolicyFile);
            args.Add("--delete-policy-after-read");
            if (hardening.ExtraHelperArguments is not null)
            {
                args.AddRange(hardening.ExtraHelperArguments);
            }

            args.Add("--");
        }

        args.Add(command.Executable);
        args.AddRange(command.Arguments);
        return args;
    }
}
