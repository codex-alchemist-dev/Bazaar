using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests;

/// <summary>Runs the real helper binary on this host. Tests that need Landlock or seccomp skip, loudly, when the kernel lacks them.</summary>
internal static class HelperHost
{
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>The apphost of the helper project, copied next to the test assembly by the project reference.</summary>
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Bazaar.Server.DevSandbox.Linux.Helper");

    /// <summary>The .NET install directory the framework-dependent helper needs as DOTNET_ROOT.</summary>
    internal static string DotnetRoot
    {
        get
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/');
            return Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        }
    }

    internal static Dictionary<string, string> StartupEnvironment => new()
    {
        ["DOTNET_ROOT"] = DotnetRoot,
        ["DOTNET_EnableDiagnostics"] = "0",
        ["DOTNET_gcServer"] = "0",
    };

    internal static bool IsLinux => OperatingSystem.IsLinux();

    private static readonly Lazy<int> _landlockAbi = new(() =>
    {
        if (!IsLinux || !File.Exists(HelperPath))
        {
            return 0;
        }

        var r = RunAsync(["--info"], null).GetAwaiter().GetResult();
        foreach (var line in r.Stdout.Split('\n'))
        {
            if (line.StartsWith("landlock-abi=", StringComparison.Ordinal) && int.TryParse(line["landlock-abi=".Length..], out var abi))
            {
                return abi;
            }
        }

        return 0;
    });

    /// <summary>The Landlock ABI this kernel reports through the helper; 0 or below means unavailable.</summary>
    internal static int LandlockAbi => _landlockAbi.Value;

    internal static string RequireSkipReason() =>
        !IsLinux ? "not Linux"
        : !File.Exists(HelperPath) ? "helper apphost was not built next to the tests: " + HelperPath
        : LandlockAbi < 1 ? "this kernel reports no Landlock (landlock_create_ruleset failed); Landlock and seccomp end-to-end tests cannot run here"
        : string.Empty;

    internal static async Task<Result> RunAsync(IReadOnlyList<string> args, string? workingDirectory, IReadOnlyDictionary<string, string>? extraEnvironment = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory ?? "/",
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        psi.Environment["PATH"] = "/usr/bin:/bin";
        foreach (var (k, v) in StartupEnvironment)
        {
            psi.Environment[k] = v;
        }

        if (extraEnvironment is not null)
        {
            foreach (var (k, v) in extraEnvironment)
            {
                psi.Environment[k] = v;
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("helper did not start");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new Result(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>A temp directory pair: a work directory (read and write) and an unrelated directory the policy never allows.</summary>
    internal sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Root = Directory.CreateTempSubdirectory("bazaar-helper-test-").FullName;
            Work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
            Outside = Directory.CreateDirectory(Path.Combine(Root, "outside")).FullName;
            File.WriteAllText(Path.Combine(Outside, "canary.txt"), "outside-secret");
            PolicyFile = Path.Combine(Root, "policy.json");
        }

        internal string Root { get; }

        internal string Work { get; }

        internal string Outside { get; }

        internal string PolicyFile { get; }

        internal static string[] DefaultReadPaths => ["/usr/bin", "/bin", "/usr/lib", "/lib", "/usr/lib64", "/lib64", "/proc/self"];

        internal string PolicyJson(IEnumerable<string>? readPaths = null, string extra = "") =>
            "{\"version\":1,\"readPaths\":[" + string.Join(",", (readPaths ?? DefaultReadPaths).Select(Quote)) + "],\"writePaths\":[" + Quote(Work) + "]" + extra + "}";

        internal Task<Result> RunAsync(IEnumerable<string> helperArgs, string? policyJson = null, IReadOnlyDictionary<string, string>? env = null)
        {
            File.WriteAllText(PolicyFile, policyJson ?? PolicyJson());
            var args = new List<string> { "--policy", PolicyFile };
            args.AddRange(helperArgs);
            return HelperHost.RunAsync(args, Work, env);
        }

        internal Task<Result> RunTargetAsync(string target, params string[] targetArgs)
        {
            var args = new List<string> { "--", target };
            args.AddRange(targetArgs);
            return RunAsync(args);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
