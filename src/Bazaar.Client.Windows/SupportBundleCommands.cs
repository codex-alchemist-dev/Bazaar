using Bazaar.Client.Core.Panes;
using Bazaar.Client.Modules;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Diagnostics.Capture;
using Bazaar.SupportBundle;

namespace Bazaar.Client.Windows;

/// <summary>
/// The headless command-line modes (doc 37 section 4d): <c>--self-check [--report path]</c> runs the start-up checks without a
/// window or a network connection and exits 0 only when all pass; <c>--support-bundle path</c> writes the redacted local zip.
/// This class only composes the <c>Bazaar.SupportBundle</c> module.
/// </summary>
public static class SupportBundleCommands
{
    private static ClientLogFile? _log;

    /// <summary>Starts copying stderr into the client log file. A failure to open the log never stops the client.</summary>
    public static void InstallLog()
    {
        try
        {
            _log = ClientLogFile.Open(ClientDataPaths.LogFile(ClientDataPaths.DataDir()), Console.Error);
            Console.SetError(_log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Bazaar: client log file unavailable; continuing without it.");
        }
    }

    /// <summary>Returns the process exit code when <paramref name="args"/> asked for a headless mode, otherwise null.</summary>
    public static int? TryRun(IReadOnlyList<string> args, Func<IPaneModule[]> modules)
    {
        var selfCheck = args.Contains("--self-check", StringComparer.Ordinal);
        var bundleIndex = IndexOf(args, "--support-bundle");
        if (!selfCheck && bundleIndex < 0)
        {
            return null;
        }

        var dataDir = ClientDataPaths.DataDir();
        var exit = 0;
        if (selfCheck)
        {
            var reportPath = ValueAfter(args, "--report") ?? Path.Combine(dataDir, "selfcheck.json");
            var report = RunSelfCheck(modules());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, SelfCheckRunner.ToJson(report));
            foreach (var failed in report.Checks.Where(c => !c.Passed))
            {
                _log?.Error($"self-check {failed.Id} failed: {failed.Reason}");
            }

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

            WriteBundle(target, dataDir, modules());
        }

        return exit;
    }

    /// <summary>Runs the head's start-up checks over <paramref name="modules"/>. No window, no network.</summary>
    public static SelfCheckReport RunSelfCheck(IPaneModule[] modules) =>
        SelfCheckRunner.Run(
            ClientVersion(),
            [new ModulesLoadCheck(modules), new PaneRegistryCheck(modules)]);

    /// <summary>Writes the support bundle zip to <paramref name="target"/>.</summary>
    public static void WriteBundle(string target, string dataDir, IPaneModule[] modules)
    {
        var loaded = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current()).Loaded;
        var info = new ClientInfo(
            ClientVersion(),
            new RuntimeClientInfoProvider(ClientVersion(), []).Current().Device,
            [.. loaded.Select(m => new ModuleInfo(m.Id, m.Version)).OrderBy(m => m.Name, StringComparer.Ordinal)]);
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
    public static string? SaveFromPalette(IPaneModule[] modules, string dataDir, TimeProvider clock, Action<string> report)
    {
        try
        {
            var path = Path.Combine(dataDir, "support", $"bazaar-support-{clock.GetUtcNow():yyyyMMdd-HHmmss}.zip");
            WriteBundle(path, dataDir, modules);
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

    private sealed class ModulesLoadCheck(IPaneModule[] modules) : ISelfCheck
    {
        public string Id => "modules.load";

        public SelfCheckResult Run()
        {
            var result = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current());
            return result.Skipped.Count == 0
                ? new SelfCheckResult(Id, true, $"{result.Loaded.Count} modules loaded")
                : new SelfCheckResult(Id, false, "skipped: " + string.Join(", ", result.Skipped.Select(s => $"{s.ModuleId} ({s.Code})")));
        }
    }

    private sealed class PaneRegistryCheck(IPaneModule[] modules) : ISelfCheck
    {
        public string Id => "panes.registry";

        public SelfCheckResult Run()
        {
            var shipped = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current()).Only(modules);
            _ = FirstPartyPanes.CreateRegistry(shipped);
            return new SelfCheckResult(Id, true, "registry built");
        }
    }
}
