using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// The environment marker that ties a CLI child to its Bazaar server (docs/design/91 section 6). Every child carries
/// <see cref="EnvName"/> with a value that stays the same across restarts of one server, so a start-up sweep can find the
/// children a crashed predecessor left behind. A command-line marker is not used because the unmodified CLI may reject it.
/// </summary>
public static class ClaudeCodeOrphanMarker
{
    public const string EnvName = "BAZAAR_CLAUDE_CODE_RUN";

    /// <summary>
    /// The configured <see cref="ClaudeCodeOptions.RunMarker"/>, else a value derived from the login folder, which one server
    /// owns alone (one login serves one human). Two servers on one machine therefore never sweep each other's children.
    /// </summary>
    public static string For(ClaudeCodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.RunMarker))
        {
            return options.RunMarker.Trim();
        }

        var seed = string.IsNullOrWhiteSpace(options.ConfigDir) ? "api-key-mode" : Path.GetFullPath(options.ConfigDir);
        return "cc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Linux start-up sweep. Kills processes that carry this server's marker, belong to the server's own user and are not the
/// server itself. At start no child of the running server exists yet, so every match is a leftover from a crash. Reads
/// <c>/proc</c> only; a process it cannot read is skipped, never guessed at.
/// </summary>
public static class LinuxOrphanSweeper
{
    /// <summary>Returns the ids of the processes that were killed.</summary>
    /// <param name="procRoot">Normally <c>/proc</c>. A test passes a folder laid out the same way.</param>
    /// <param name="marker">The value of <see cref="ClaudeCodeOrphanMarker.EnvName"/> to look for.</param>
    /// <param name="selfPid">The server's own process id, never killed.</param>
    /// <param name="ownUid">The server's user id; processes of other users are left alone.</param>
    /// <param name="kill">Kills one process by id.</param>
    public static IReadOnlyList<int> Sweep(string procRoot, string marker, int selfPid, uint ownUid, Action<int> kill)
    {
        ArgumentException.ThrowIfNullOrEmpty(procRoot);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        ArgumentNullException.ThrowIfNull(kill);
        var killed = new List<int>();
        if (!Directory.Exists(procRoot))
        {
            return killed;
        }

        var wanted = $"{ClaudeCodeOrphanMarker.EnvName}={marker}";
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == selfPid || pid <= 1)
            {
                continue;
            }

            try
            {
                if (ReadUid(Path.Combine(dir, "status")) != ownUid)
                {
                    continue;
                }

                var environ = File.ReadAllBytes(Path.Combine(dir, "environ"));
                if (!ContainsEntry(environ, wanted))
                {
                    continue;
                }

                kill(pid);
                killed.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // Gone already, or not ours to read: skip it.
            }
        }

        return killed;
    }

    /// <summary>The real user id from a <c>status</c> file, or null when it cannot be read.</summary>
    public static uint? ReadUid(string statusPath)
    {
        foreach (var line in File.ReadLines(statusPath))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
            }
        }

        return null;
    }

    private static bool ContainsEntry(byte[] environ, string entry)
    {
        var text = Encoding.UTF8.GetString(environ);
        return text.Split('\0').Contains(entry, StringComparer.Ordinal);
    }
}

/// <summary>Runs <see cref="LinuxOrphanSweeper"/> once at host start. Does nothing on other systems.</summary>
public sealed class ClaudeCodeOrphanSweepService(ClaudeCodeOptions options, ILogger<ClaudeCodeOrphanSweepService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Task.CompletedTask;
        }

        var ownUid = LinuxOrphanSweeper.ReadUid("/proc/self/status");
        if (ownUid is null)
        {
            return Task.CompletedTask;
        }

        var killed = LinuxOrphanSweeper.Sweep(
            "/proc",
            ClaudeCodeOrphanMarker.For(options),
            Environment.ProcessId,
            ownUid.Value,
            pid =>
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            });
        if (killed.Count > 0)
        {
            Log.Swept(logger, killed.Count);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static class Log
    {
        private static readonly Action<ILogger, int, Exception?> _swept = LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(1, nameof(Swept)),
            "Claude Code start-up sweep removed {Count} leftover process(es) from an earlier run.");

        public static void Swept(ILogger logger, int count) => _swept(logger, count, null);
    }
}
