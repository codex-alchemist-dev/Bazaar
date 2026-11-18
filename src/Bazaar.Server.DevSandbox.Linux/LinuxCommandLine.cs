using System.Globalization;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>Builds the <c>prlimit ... -- unshare ... -- command</c> argument vector. Pure; no shell is involved.</summary>
internal static class LinuxCommandLine
{
    internal static readonly string[] UnshareFlags =
    [
        "--user", "--map-root-user", "--net", "--pid", "--mount", "--ipc", "--uts", "--fork", "--kill-child", "--mount-proc",
    ];

    internal static IReadOnlyList<string> BuildArguments(
        LinuxConfinementOptions options,
        string unsharePath,
        int memoryLimitMb,
        int cpuSeconds,
        LinuxLaunchCommand command)
    {
        var args = new List<string>
        {
            "--as=" + checked((long)memoryLimitMb * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            "--cpu=" + cpuSeconds.ToString(CultureInfo.InvariantCulture),
            "--nproc=" + options.MaxProcesses.ToString(CultureInfo.InvariantCulture),
            "--nofile=" + options.MaxOpenFiles.ToString(CultureInfo.InvariantCulture),
            "--",
            unsharePath,
        };
        args.AddRange(UnshareFlags);
        args.Add("--");
        args.Add(command.Executable);
        args.AddRange(command.Arguments);
        return args;
    }
}
