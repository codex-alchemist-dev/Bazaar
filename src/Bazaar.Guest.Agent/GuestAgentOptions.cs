using System.Globalization;
using Bazaar.Vms.Sockets;

namespace Bazaar.Guest.Agent;

/// <summary>Settings for one agent run. Production builds them from the command line; tests build them directly.</summary>
public sealed record GuestAgentOptions
{
    /// <summary>Default program for a terminal: the POSIX shell on Linux.</summary>
    public static string DefaultShell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    /// <summary>VM id the host provisioned this guest for. Echoed in Hello.</summary>
    public required string VmId { get; init; }

    /// <summary>Fixed per-VM value the host chose at provisioning. Echoed in Hello so the host can check it reached the guest it prepared. It never changes, so it gives no replay protection.</summary>
    public required string Nonce { get; init; }

    /// <summary>Directories file transfers may use. Absolute paths.</summary>
    public required IReadOnlyList<string> AllowedRoots { get; init; }

    /// <summary>Program that PtyOpen starts.</summary>
    public string ShellPath { get; init; } = DefaultShell;

    /// <summary>Execs allowed to run at once. An Exec over the cap is refused.</summary>
    public int MaxConcurrentExecs { get; init; } = 4;

    /// <summary>Gap between the heartbeats the agent sends on its own.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Byte channel to the host. Stdio unless the boot unit says otherwise.</summary>
    public GuestTransportKind Transport { get; init; } = GuestTransportKind.Stdio;

    /// <summary>Hyper-V service GUID to dial for <see cref="GuestTransportKind.HvSocket"/>. The default matches the host listener's default.</summary>
    public Guid HvSocketServiceId { get; init; } = HvSocketAddress.DefaultServiceId;

    /// <summary>Port to dial for <see cref="GuestTransportKind.Vsock"/>. The default matches the host listener's default.</summary>
    public uint VsockPort { get; init; } = HvSocketAddress.DefaultPort;

    /// <summary>CID to dial for <see cref="GuestTransportKind.Vsock"/>. 2 is the host.</summary>
    public uint VsockHostCid { get; init; } = 2;

    /// <summary>How long a socket transport keeps retrying to reach the host before the agent gives up.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Agent build string sent in Hello.</summary>
    public string AgentVersion { get; init; } = typeof(GuestAgentOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Environment variable that can carry the Hello nonce. It is removed from the process environment once read.</summary>
    public const string NonceEnvironmentVariable = "BAZAAR_GUEST_NONCE";

    /// <summary>
    /// Parses the command line. Options: <c>--vm-id</c> (required), the Hello nonce from exactly one of <c>--nonce-file</c>, the
    /// <c>BAZAAR_GUEST_NONCE</c> environment variable or <c>--nonce</c> (see the remarks), <c>--root</c> (repeatable,
    /// absolute, at least one), <c>--shell</c>, <c>--max-execs</c>, <c>--heartbeat-seconds</c>, <c>--transport</c>
    /// (<c>stdio</c>, <c>hvsock</c> or <c>vsock</c>; default <c>stdio</c>), <c>--hvsock-service-id</c>, <c>--vsock-port</c>, <c>--vsock-cid</c>,
    /// <c>--connect-timeout-seconds</c>.
    /// </summary>
    /// <remarks>
    /// VM id and nonce come from the command line, not from the image. The golden image is shared by every VM of a
    /// kind, so the boot unit that starts the agent passes the per-VM values the host chose at provisioning.
    /// <para>
    /// <b><c>--nonce</c> is visible to every other process in the guest</b> (<c>/proc/&lt;pid&gt;/cmdline</c> is world-readable on Linux, and process
    /// listings show it on Windows). Prefer <c>--nonce-file &lt;path&gt;</c>: the file is read, one trailing newline is trimmed, it must not be empty, and
    /// the agent then tries to delete it (a file that cannot be deleted is only logged). Next best is <c>BAZAAR_GUEST_NONCE</c>, which is removed from
    /// the agent's environment once read so no child inherits it. Precedence of what is accepted is file, then environment, then <c>--nonce</c>, but
    /// only one source may be given. <c>--nonce</c> stays for tests and for stdio and Windows Sandbox runs where the guest is not shared.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, a required option is missing, or the nonce is given twice.</exception>
    public static GuestAgentOptions Parse(IReadOnlyList<string> args) => Parse(args, null, null);

    /// <summary>Parses the command line with the nonce environment variable and the log made explicit, so tests do not touch the process environment.</summary>
    internal static GuestAgentOptions Parse(IReadOnlyList<string> args, INonceEnvironment? environment, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(args);
        environment ??= ProcessNonceEnvironment.Instance;
        log ??= Console.Error;
        string? nonceFile = null;

        string? vmId = null;
        string? nonce = null;
        string? shell = null;
        var roots = new List<string>();
        var maxExecs = 4;
        var heartbeatSeconds = 30;
        var transport = GuestTransportKind.Stdio;
        var serviceId = HvSocketAddress.DefaultServiceId;
        var vsockPort = HvSocketAddress.DefaultPort;
        uint vsockCid = 2;
        var connectSeconds = 60;

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
                case "--nonce-file":
                    nonceFile = value;
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
                case "--transport":
                    transport = value switch
                    {
                        "stdio" => GuestTransportKind.Stdio,
                        "hvsock" => GuestTransportKind.HvSocket,
                        "vsock" => GuestTransportKind.Vsock,
                        _ => throw new ArgumentException("--transport must be stdio, hvsock or vsock."),
                    };
                    break;
                case "--hvsock-service-id":
                    serviceId = Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
                        ? parsed
                        : throw new ArgumentException("--hvsock-service-id must be a GUID.");
                    break;
                case "--vsock-port":
                    vsockPort = (uint)ParsePositive(name, value);
                    break;
                case "--vsock-cid":
                    vsockCid = (uint)ParsePositive(name, value);
                    break;
                case "--connect-timeout-seconds":
                    connectSeconds = ParsePositive(name, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (string.IsNullOrEmpty(vmId))
        {
            throw new ArgumentException("--vm-id is required.");
        }

        nonce = ResolveNonce(nonce, nonceFile, environment, log);

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
            Transport = transport,
            HvSocketServiceId = serviceId,
            VsockPort = vsockPort,
            VsockHostCid = vsockCid,
            ConnectTimeout = TimeSpan.FromSeconds(connectSeconds),
        };
    }

    /// <summary>
    /// Picks the one nonce source. The environment variable is removed whenever it is present, even when another source was also given (which is then
    /// an error), so a refused start does not leave the nonce in the environment.
    /// </summary>
    private static string ResolveNonce(string? argument, string? file, INonceEnvironment environment, TextWriter log)
    {
        var fromEnvironment = environment.Take(NonceEnvironmentVariable);
        if (fromEnvironment is { Length: 0 })
        {
            fromEnvironment = null;
        }

        var given = (argument is not null ? 1 : 0) + (file is not null ? 1 : 0) + (fromEnvironment is not null ? 1 : 0);
        if (given > 1)
        {
            throw new ArgumentException("Give the nonce through exactly one of --nonce-file, BAZAAR_GUEST_NONCE or --nonce.");
        }

        string? nonce;
        if (file is not null)
        {
            nonce = ReadNonceFile(file, log);
        }
        else
        {
            nonce = fromEnvironment ?? argument;
        }

        return string.IsNullOrEmpty(nonce)
            ? throw new ArgumentException("A nonce is required: --nonce-file, BAZAAR_GUEST_NONCE or --nonce, and it must not be empty.")
            : nonce;
    }

    private static string ReadNonceFile(string path, TextWriter log)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException($"--nonce-file '{path}' could not be read: {ex.Message}");
        }

        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text = text[..^2];
        }
        else if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"bazaar-guest-agent: could not delete the nonce file '{path}': {ex.Message}");
        }

        return text;
    }

    private static int ParsePositive(string name, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException($"{name} must be a positive whole number.");
}

/// <summary>Where the agent reads and clears the nonce environment variable. A seam so tests leave the process environment alone.</summary>
internal interface INonceEnvironment
{
    /// <summary>Returns the variable's value and removes it, or null when it is not set.</summary>
    string? Take(string name);
}

internal sealed class ProcessNonceEnvironment : INonceEnvironment
{
    public static ProcessNonceEnvironment Instance { get; } = new();

    public string? Take(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        return value;
    }
}
