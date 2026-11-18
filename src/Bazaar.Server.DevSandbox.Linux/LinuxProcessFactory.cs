using System.Diagnostics;

namespace Bazaar.Server.DevSandbox.Linux;

internal static class LinuxProcessFactory
{
    /// <summary>Start info with an argument vector (no shell), a scrubbed environment (only PATH), piped stdio and the given cwd.</summary>
    internal static ProcessStartInfo CreateStartInfo(LinuxConfinementOptions options, string file, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        psi.Environment["PATH"] = options.Path;
        return psi;
    }
}
