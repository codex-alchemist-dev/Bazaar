using System.Globalization;
using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet;

/// <summary>
/// Everything the launcher needs to start a managed Garnet: the binary and its command-line arguments.
/// <para>
/// Limitation: Garnet reads its password only from a command-line switch (<c>--password</c>) or from its configuration file; it does not
/// read it from standard input or an environment variable. A file would put the secret on disk, so the password goes on the command line.
/// It is random per host run, the server listens on loopback only, and the command line is visible only to processes of the same
/// Windows account or administrators, who could read the host's memory anyway. The password is never logged: <see cref="ToString"/>
/// redacts it, and the supervisor logs exception types only.
/// </para>
/// </summary>
public sealed class GarnetLaunchSpec
{
    /// <summary>Creates the spec.</summary>
    public GarnetLaunchSpec(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        Executable = executable;
        Arguments = arguments;
    }

    /// <summary>The server binary.</summary>
    public string Executable { get; }

    /// <summary>The server arguments, including the password. Never log them.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Builds the arguments for one child: loopback only, password auth, the memory size, and an append-only file with checkpoints in the
    /// data directory. <paramref name="recover"/> adds <c>-r</c> (recover from the latest checkpoint and append-only file); the
    /// supervisor sets it when it restarts a child, not on the first start of a host run.
    /// </summary>
    public static GarnetLaunchSpec Build(GarnetCacheOptions options, int port, string password, bool recover)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(password);

        List<string> arguments =
        [
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--bind", "127.0.0.1",
            "--auth", "Password",
            "--password", password,
            "--memory", options.MaxMemory,
            "--aof",
            "--logdir", options.DataDirectory,
            "--checkpointdir", Path.Combine(options.DataDirectory, "checkpoints"),
            "--checkpointdir", options.DataDirectory,
        ];
        if (options.AofCommitWait)
        {
            arguments.Add("--aof-commit-wait");
        }

        if (recover)
        {
            arguments.Add("-r");
        }

        return new GarnetLaunchSpec(options.BinaryPath, arguments);
    }

    /// <summary>Never includes the arguments (they hold the password).</summary>
    public override string ToString() => $"garnet launch of {Executable} (arguments redacted)";
}

/// <summary>Starts a managed Garnet child. The real implementation runs a process; tests inject a fake.</summary>
public interface IGarnetProcessLauncher
{
    /// <summary>Starts the child. Throws when it cannot start; the supervisor counts that as a failed attempt.</summary>
    Task<IValkeyChild> StartAsync(GarnetLaunchSpec spec, CancellationToken ct);
}
