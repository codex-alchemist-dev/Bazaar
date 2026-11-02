using System.Globalization;

namespace Bazaar.Guest.Agent;

/// <summary>Settings for one agent run. Production builds them from the command line; tests build them directly.</summary>
public sealed record GuestAgentOptions
{
    /// <summary>Default program for a terminal: the POSIX shell on Linux.</summary>
    public static string DefaultShell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    /// <summary>VM id the host provisioned this guest for. Echoed in Hello.</summary>
    public required string VmId { get; init; }

    /// <summary>Host-supplied nonce. Echoed in Hello so the host can reject a replayed hello.</summary>
    public required string Nonce { get; init; }

    /// <summary>Directories file transfers may use. Absolute paths.</summary>
    public required IReadOnlyList<string> AllowedRoots { get; init; }

    /// <summary>Program that PtyOpen starts.</summary>
    public string ShellPath { get; init; } = DefaultShell;

    /// <summary>Execs allowed to run at once. An Exec over the cap is refused.</summary>
    public int MaxConcurrentExecs { get; init; } = 4;

    /// <summary>Gap between the heartbeats the agent sends on its own.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Agent build string sent in Hello.</summary>
    public string AgentVersion { get; init; } = typeof(GuestAgentOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Parses the command line. Options: <c>--vm-id</c> and <c>--nonce</c> (both required), <c>--root</c> (repeatable,
    /// absolute, at least one), <c>--shell</c>, <c>--max-execs</c>, <c>--heartbeat-seconds</c>.
    /// </summary>
    /// <remarks>
    /// VM id and nonce come from the command line, not from the image. The golden image is shared by every VM of a
    /// kind, so the boot unit that starts the agent passes the per-VM values the host chose at provisioning.
    /// </remarks>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, or a required option is missing.</exception>
    public static GuestAgentOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? vmId = null;
        string? nonce = null;
        string? shell = null;
        var roots = new List<string>();
        var maxExecs = 4;
        var heartbeatSeconds = 30;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"Option '{name}' needs a value.");
            }

            var value = args[++i];
            switch (name)
            {
                case "--vm-id":
                    vmId = value;
                    break;
                case "--nonce":
                    nonce = value;
                    break;
                case "--root":
                    roots.Add(value);
                    break;
                case "--shell":
                    shell = value;
                    break;
                case "--max-execs":
                    maxExecs = ParsePositive(name, value);
                    break;
                case "--heartbeat-seconds":
                    heartbeatSeconds = ParsePositive(name, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (string.IsNullOrEmpty(vmId))
        {
            throw new ArgumentException("--vm-id is required.");
        }

        if (string.IsNullOrEmpty(nonce))
        {
            throw new ArgumentException("--nonce is required.");
        }

        if (roots.Count == 0)
        {
            throw new ArgumentException("At least one --root is required.");
        }

        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root))
            {
                throw new ArgumentException($"--root '{root}' must be an absolute path.");
            }
        }

        return new GuestAgentOptions
        {
            VmId = vmId,
            Nonce = nonce,
            AllowedRoots = roots.Select(root => Path.GetFullPath(root)).ToArray(),
            ShellPath = shell ?? DefaultShell,
            MaxConcurrentExecs = maxExecs,
            HeartbeatInterval = TimeSpan.FromSeconds(heartbeatSeconds),
        };
    }

    private static int ParsePositive(string name, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException($"{name} must be a positive whole number.");
}
