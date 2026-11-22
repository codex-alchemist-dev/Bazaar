using System.Text.RegularExpressions;

namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>
/// Default <see cref="IRootCommandRunner"/>: runs <c>nft</c> through <see cref="IHostCommandRunner"/> and only for the two argument
/// lists the enforcer uses, <c>-f -</c> (ruleset on standard input) and <c>list table bridge bazaar_egress_&lt;tag&gt;</c>.
/// Anything else is refused before a process is started.
/// </summary>
public sealed partial class NftRootCommandRunner : IRootCommandRunner
{
    private readonly IHostCommandRunner _runner;

    /// <summary>Creates the runner over <paramref name="runner"/>.</summary>
    public NftRootCommandRunner(IHostCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc />
    public Task<HostCommandResult> RunAsync(RootCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAllowed(command.Arguments))
        {
            throw new ArgumentException("These nft arguments are not allowed.", nameof(command));
        }

        return _runner.RunAsync(new HostCommand(HostTool.Nft, command.Arguments, command.StandardInput), ct);
    }

    internal static bool IsAllowed(IReadOnlyList<string> args) =>
        args is ["-f", "-"] || (args is ["list", "table", "bridge", var table] && TablePattern().IsMatch(table));

    [GeneratedRegex(@"\Abazaar_egress_[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TablePattern();
}
