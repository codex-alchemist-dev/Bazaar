using System.Diagnostics;

namespace Bazaar.Server.DevSandbox.Linux;

internal static class LinuxProcessFactory
{
    /// <summary>Start info with an argument vector (no shell), a scrubbed environment (only PATH, plus the helper's start-up variables when hardening is on), piped stdio and the given cwd.</summary>
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
        if (options.HelperPath is not null && options.HelperEnvironment is not null)
        {
            // For the helper's own start-up only; the helper gives its target an environment holding just PATH.
            foreach (var (name, value) in options.HelperEnvironment)
            {
                psi.Environment[name] = value;
            }
        }
        return psi;
    }
}
