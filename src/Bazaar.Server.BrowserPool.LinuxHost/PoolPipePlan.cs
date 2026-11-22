using System.Text.RegularExpressions;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>
/// The pure parts of the DevTools pipe: the FIFO names, the path checks, and the launch argv with the spawn tool in front. No I/O.
/// </summary>
internal static partial class PoolPipePlan
{
    /// <summary>The longest session id accepted for a file name.</summary>
    public const int MaxSessionIdLength = 64;

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex(@"^/[A-Za-z0-9._+\-]+(/[A-Za-z0-9._+\-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafePathPattern();

    /// <summary>True when the path is absolute, uses only letters, digits and <c>. _ + -</c>, has no empty, <c>.</c> or <c>..</c> segment and no trailing slash.</summary>
    public static bool IsSafeAbsolutePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || !SafePathPattern().IsMatch(path))
        {
            return false;
        }

        return !path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s is "." or "..");
    }

    /// <summary>True when the id may be used in a file name: 1 to 64 ASCII letters or digits.</summary>
    public static bool IsValidSessionId(string? sessionId) => sessionId is not null && SessionIdPattern().IsMatch(sessionId);

    /// <summary>The FIFO the host writes and the browser reads as descriptor 3, and the FIFO the browser writes as descriptor 4.</summary>
    public static (string Commands, string Replies) FifoPaths(string pipeDirectory, string sessionId)
    {
        if (!IsSafeAbsolutePath(pipeDirectory))
        {
            throw new ArgumentException("The pipe directory is not acceptable.", nameof(pipeDirectory));
        }

        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException("The session id cannot be used in a file name.", nameof(sessionId));
        }

        return (pipeDirectory + "/" + sessionId + "-fd3", pipeDirectory + "/" + sessionId + "-fd4");
    }

    /// <summary>True when the two absolute paths are the same or one is inside the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        var x = a.TrimEnd('/') + "/";
        var y = b.TrimEnd('/') + "/";
        return x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first launch element that is a path equal to, above or below the pipe directory (for example a <c>--bind</c> source), or
    /// null. Such a launch is refused: the sandbox must never see the FIFOs' directory.
    /// </summary>
    public static string? FindConflict(string pipeDirectory, IReadOnlyList<string> launch)
    {
        foreach (var element in launch)
        {
            if (element.Length > 0 && element[0] == '/' && !element.Contains(' ', StringComparison.Ordinal) && Overlaps(pipeDirectory, element))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>
    /// The full argv: <c>helper --fd3 PATH --fd4 PATH -- launch...</c>. The launch argv is not changed, except that a program name
    /// that is not absolute is replaced by its absolute path, because the helper uses execve and never searches a path.
    /// </summary>
    /// <param name="helperPath">Absolute path of the spawn tool.</param>
    /// <param name="commandsFifo">Path of the FIFO for descriptor 3.</param>
    /// <param name="repliesFifo">Path of the FIFO for descriptor 4.</param>
    /// <param name="launch">The launch argv (program first).</param>
    /// <param name="resolve">Finds the absolute path of a bare program name, or null.</param>
    public static IReadOnlyList<string> Compose(string helperPath, string commandsFifo, string repliesFifo, IReadOnlyList<string> launch, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(resolve);
        if (launch.Count == 0)
        {
            throw new ArgumentException("The launch command is empty.", nameof(launch));
        }

        var program = launch[0];
        if (program.Length == 0 || program[0] != '/')
        {
            program = resolve(program) ?? throw new PoolHostException("The launch program " + (program.Length == 0 ? "(empty)" : program) + " was not found on the search path.");
            if (program.Length == 0 || program[0] != '/')
            {
                throw new PoolHostException("The launch program did not resolve to an absolute path.");
            }
        }

        var argv = new List<string>(launch.Count + 6) { helperPath, "--fd3", commandsFifo, "--fd4", repliesFifo, "--", program };
        for (var i = 1; i < launch.Count; i++)
        {
            argv.Add(launch[i]);
        }

        return argv;
    }

    /// <summary>Finds a bare program in the absolute directories of a search path (<c>:</c> separated), like a shell would, or null.</summary>
    public static string? ResolveOnPath(string program, string? searchPath, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrEmpty(program) || program.Contains('/', StringComparison.Ordinal) || string.IsNullOrEmpty(searchPath))
        {
            return null;
        }

        foreach (var dir in searchPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir[0] != '/')
            {
                continue;
            }

            var candidate = dir.TrimEnd('/') + "/" + program;
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
