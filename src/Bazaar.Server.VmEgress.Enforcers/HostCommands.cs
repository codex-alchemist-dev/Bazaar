namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>The only host tools this module may run. The runner maps each to a fixed absolute path.</summary>
public enum HostTool
{
    /// <summary>Windows PowerShell under System32 (Windows only).</summary>
    PowerShell,

    /// <summary><c>nft</c> from <c>/usr/sbin</c> or <c>/sbin</c> (Linux only).</summary>
    Nft,
}

/// <summary>One command: a tool, its argv (never a shell line), text for standard input and environment additions.</summary>
public sealed record HostCommand(
    HostTool Tool,
    IReadOnlyList<string> Arguments,
    string StandardInput,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>The outcome of a <see cref="HostCommand"/>. Output is capped by the runner.</summary>
public sealed record HostCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs one allow-listed host tool with no shell. The seam that tests replace with a fake.</summary>
public interface IHostCommandRunner
{
    /// <summary>Runs the command. Throws <see cref="EgressEnforcementException"/> when it cannot start, times out or exceeds the output cap.</summary>
    Task<HostCommandResult> RunAsync(HostCommand command, CancellationToken ct = default);
}

/// <summary>One <c>nft</c> invocation that needs root. The tool is always <c>nft</c>; only the arguments and standard input vary.</summary>
public sealed record RootCommand(IReadOnlyList<string> Arguments, string StandardInput);

/// <summary>
/// Seam for running <c>nft</c> with the privilege it needs. The default implementation (<see cref="NftRootCommandRunner"/>) runs
/// it directly, which works when the server has CAP_NET_ADMIN; a host with a root helper (for example a sudo rule scoped to
/// these two argument lists) supplies its own implementation.
/// </summary>
public interface IRootCommandRunner
{
    /// <summary>Runs <c>nft</c> with the arguments, after the allow-list check. Throws when the arguments are not allowed.</summary>
    Task<HostCommandResult> RunAsync(RootCommand command, CancellationToken ct = default);
}
