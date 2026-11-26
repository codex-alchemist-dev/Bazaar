using Bazaar.Diagnostics.Capture;
using Bazaar.SupportBundle;

namespace Bazaar.Client.Linux;

/// <summary>
/// The headless command-line modes of doc 37 section 4d for the Linux head: <c>--self-check [--report path]</c> and
/// <c>--support-bundle path</c>. Both run before the single-instance forward and any window. Composition only; the work
/// is in <c>Bazaar.SupportBundle</c>.
/// </summary>
public static class SupportBundleCommands
{
    /// <summary>Returns the process exit code when <paramref name="args"/> asked for a headless mode, otherwise null.</summary>
    public static int? TryRun(IReadOnlyList<string> args, string dataDir)
    {
        var selfCheck = args.Contains("--self-check", StringComparer.Ordinal);
        var bundleIndex = IndexOf(args, "--support-bundle");
        if (!selfCheck && bundleIndex < 0)
        {
            return null;
        }

        var exit = 0;
        if (selfCheck)
        {
            var report = RunSelfCheck(dataDir);
            var reportPath = ValueAfter(args, "--report") ?? Path.Combine(dataDir, "selfcheck.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, SelfCheckRunner.ToJson(report));
            exit = report.Passed ? 0 : 1;
        }

        if (bundleIndex >= 0)
        {
            var target = bundleIndex + 1 < args.Count ? args[bundleIndex + 1] : null;
            if (string.IsNullOrWhiteSpace(target))
            {
                Console.Error.WriteLine("Bazaar: --support-bundle needs a path for the zip file.");
                return 2;
            }

            WriteBundle(target, dataDir);
        }

        return exit;
    }

    /// <summary>Runs the Linux head's start-up checks. No window, no network.</summary>
    public static SelfCheckReport RunSelfCheck(string dataDir) =>
        SelfCheckRunner.Run(ClientVersion(), [new DataDirWritableCheck(dataDir)]);

    /// <summary>Writes the support bundle zip to <paramref name="target"/>.</summary>
    public static void WriteBundle(string target, string dataDir)
    {
        var info = new ClientInfo(
            ClientVersion(),
            new RuntimeClientInfoProvider(ClientVersion(), []).Current().Device,
            []);
        var builder = new SupportBundleBuilder([Environment.MachineName, Environment.UserName]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        using var output = File.Create(target);
        builder.Build(
            [
                new ClientInfoSource(info),
                new FileSource("client-log", ClientDataPaths.LogFile(dataDir), "logs/client.log"),
                new FileSource("self-check", Path.Combine(dataDir, "selfcheck.json"), "selfcheck.json"),
            ],
            output);
    }

    /// <summary>
    /// Runs the palette command: writes <c>support/bazaar-support-&lt;time&gt;.zip</c> under the data directory and reports the
    /// path (or the failure) through <paramref name="report"/>. Returns the path, or null when it failed.
    /// </summary>
    public static string? SaveFromPalette(string dataDir, TimeProvider clock, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            var path = Path.Combine(dataDir, "support", $"bazaar-support-{clock.GetUtcNow():yyyyMMdd-HHmmss}.zip");
            WriteBundle(path, dataDir);
            report($"Bazaar: support bundle saved to {path}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report($"Bazaar: could not save the support bundle ({ex.GetType().Name}).");
            return null;
        }
    }

    private static string ClientVersion() =>
        typeof(SupportBundleCommands).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    private static int IndexOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        var i = IndexOf(args, name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private sealed class DataDirWritableCheck(string dataDir) : ISelfCheck
    {
        public string Id => "data.writable";

        public SelfCheckResult Run()
        {
            Directory.CreateDirectory(dataDir);
            var probe = Path.Combine(dataDir, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new SelfCheckResult(Id, true, "data directory is writable");
        }
    }
}
